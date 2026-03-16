using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NvimWinFormsGui.NvimRpc;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace NvimWinFormsGui;

/// <summary>
/// Neovim GUI (UI attach / msgpack-rpc) implementation.
/// Renders Neovim ext_linegrid in a WebView2 canvas (no xterm).
/// </summary>
public sealed class MainForm : Form
{
    // ===== Config =====
    private const string NvimExe = "nvim";
    private const int InitialWidth = 1100;
    private const int InitialHeight = 750;

    private const int InitialSplitter = 280;
    private const int Panel1Min = 220;
    private const int Panel2Min = 400;

    private const int MaxExpandedDirsToSave = 100;

    private enum OpenMode { NewTab, Current, VSplit, Split }
    private OpenMode _openMode = OpenMode.NewTab;

    // ===== UI =====
    private readonly ToolStripContainer _tsc = new() { Dock = DockStyle.Fill };
    private readonly MenuStrip _menu = new() { Dock = DockStyle.None };

    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
    };
    private bool _splitConstraintsApplied;

    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
        ShowNodeToolTips = true,
    };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private readonly ContextMenuStrip _treeMenu = new();

    // ===== Explorer icons =====
    private readonly ImageList _icons = new()
    {
        ImageSize = new Size(16, 16),
        ColorDepth = ColorDepth.Depth32Bit
    };
    private const string IconKeyFolder = "__folder__";
    private const string IconKeyFolderOpen = "__folder_open__";
    private readonly Dictionary<string, string> _fileIconKeyByExt = new(StringComparer.OrdinalIgnoreCase);
    private int _fileIconSeq = 0;

    // ===== Tree tags =====
    private sealed record FsTag(string FullPath, bool IsDirectory);
    private static readonly FsTag DummyTag = new("__dummy__", true);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_OPENICON = 0x000000002;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private readonly ConcurrentDictionary<string, byte> _loadingDirs = new(StringComparer.OrdinalIgnoreCase);
    private string _treeRootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ===== Persist =====
    private readonly string _stateFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "NvimWinFormsGui", "tree_state.json");

    private bool _restoreInProgress;

    // ===== Neovim RPC =====
    private NvimRpcClient? _nvim;
    private bool _uiAttached;
    private int _lastCols = 80, _lastRows = 24;
    private readonly ConcurrentQueue<IReadOnlyList<object?>> _redrawQueue = new();
    private readonly System.Windows.Forms.Timer _redrawTimer = new() { Interval = 33 }; // ~30fps
    private volatile bool _redrawSerializeInFlight;
    private volatile bool _webReady;
    private volatile bool _firstRedrawSeen;
    private int _restoreScheduled;

    // Buffer state for Save/SaveAs
    private bool _currentBufferHasFile;
    private string _currentSuggestedFileName = "untitled.txt";

    private CoreWebView2Environment? _wvEnv;
    private Task? _webInitTask;
    private int _webInitStarted;

    private CancellationTokenSource? _pipeCts;
    private readonly string[] _startupArgs;
    private int _startupArgsOpened;

    public MainForm(string[] args)
    {
        _startupArgs = args ?? Array.Empty<string>();

        Text = "Neovim (UI attach)";
        Width = InitialWidth;
        Height = InitialHeight;
        MinimumSize = new Size(Panel1Min + Panel2Min + 80, 520);

        BuildMenu();

        Controls.Add(_tsc);
        MainMenuStrip = _menu;

        _tsc.TopToolStripPanel.Controls.Add(_menu);
        _tsc.ContentPanel.Padding = Padding.Empty;
        _tsc.ContentPanel.Margin = Padding.Empty;
        _tsc.ContentPanel.Controls.Add(_split);

        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_web);

        _web.Margin = Padding.Empty;
        _web.Padding = Padding.Empty;

        SetupExplorerIcons();
        SetupExplorerTree();
        SetupExplorerContextMenu();

        _redrawTimer.Tick += async (_, __) => await FlushRedrawAsync();

        Resize += (_, __) => ClampSplitterDistance();
        _split.SplitterMoved += (_, __) => ClampSplitterDistance();

        Load += (_, __) =>
        {
            BeginInvoke(() => ApplySplitConstraintsAndDistance(InitialSplitter));
            StartWebViewInitialization();
        };

        Shown += (_, __) =>
        {
            BeginInvoke(() => ApplySplitConstraintsAndDistance(InitialSplitter));
        };

        FormClosing += (_, __) => SaveTreeState();
        FormClosed += (_, __) =>
        {
            try { _pipeCts?.Cancel(); } catch { }
            try { _nvim?.Dispose(); } catch { }
        };
    }

    // =========================================================
    // WebView2 init
    // =========================================================
    private async Task InitializeWebViewAsync()
    {
        try
        {
            await PrepareWebViewEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(_wvEnv);

            if (_web.CoreWebView2 is null)
                throw new InvalidOperationException("CoreWebView2 の初期化に失敗しました。");

            _web.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            _web.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            var html = ReadEmbeddedText("NvimWinFormsGui.wwwroot.index.html");

            _web.NavigateToString(html);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 の初期化に失敗しました:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        var asm = typeof(MainForm).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"埋め込みリソースが見つかりません: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void StartWebViewInitialization()
    {
        if (System.Threading.Interlocked.Exchange(ref _webInitStarted, 1) != 0)
            return;

        _webInitTask = InitializeWebViewAsync();
    }

    private async Task PrepareWebViewEnvironmentAsync()
    {
        if (_wvEnv != null) return;

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NvimWinFormsGui",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        _wvEnv = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: new CoreWebView2EnvironmentOptions());
    }

    // =========================================================
    // SplitContainer safe init
    // =========================================================
    private void ApplySplitConstraintsAndDistance(int desired)
    {
        if (_split.ClientSize.Width <= 0)
        {
            BeginInvoke(() => ApplySplitConstraintsAndDistance(desired));
            return;
        }

        if (!_splitConstraintsApplied)
        {
            _split.Panel1MinSize = Panel1Min;
            _split.Panel2MinSize = Panel2Min;
            _splitConstraintsApplied = true;
        }

        int width = _split.ClientSize.Width;
        int min = _split.Panel1MinSize;
        int max = width - _split.Panel2MinSize;
        if (max < min) max = min;

        _split.SplitterDistance = Math.Clamp(desired, min, max);
    }

    private void ClampSplitterDistance()
    {
        if (!_splitConstraintsApplied) return;
        if (_split.ClientSize.Width <= 0) return;

        int width = _split.ClientSize.Width;
        int min = _split.Panel1MinSize;
        int max = width - _split.Panel2MinSize;
        if (max < min) max = min;

        int cur = _split.SplitterDistance;
        int clamped = Math.Clamp(cur, min, max);
        if (clamped != cur) _split.SplitterDistance = clamped;
    }

    // =========================================================
    // WebView2 messages
    // =========================================================
    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebMsg? msg;
        try { msg = JsonSerializer.Deserialize<WebMsg>(e.TryGetWebMessageAsString()); }
        catch { return; }
        if (msg?.type is null) return;

        switch (msg.type)
        {
            case "ready":
                _webReady = true;
                if (msg.cols > 0 && msg.rows > 0)
                {
                    _lastCols = msg.cols;
                    _lastRows = msg.rows;
                }

                EnsureNvimStartedAndAttach();
                OpenStartupArgsOnce();

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _web.Focus();
                        ActiveControl = _web;
                    }
                    catch
                    {
                    }
                }));

                return;

            case "resize":
                if (msg.cols > 0 && msg.rows > 0)
                {
                    _lastCols = msg.cols;
                    _lastRows = msg.rows;
                    if (_uiAttached && _nvim is not null)
                    {
                        try { await _nvim.CallAsync("nvim_ui_try_resize", (long)_lastCols, (long)_lastRows); }
                        catch { }
                    }
                }
                return;

            case "input":
                if (_nvim is null || string.IsNullOrEmpty(msg.data)) return;
                await SendInputAsync(msg.data!, msg.termcode == true);
                return;

            case "mouse":
                if (_nvim is null) return;
                if (string.IsNullOrWhiteSpace(msg.button) || string.IsNullOrWhiteSpace(msg.action)) return;
                await SendMouseInputAsync(
                    msg.button!,
                    msg.action!,
                    msg.modifiers ?? "",
                    msg.grid,
                    msg.row,
                    msg.col);
                return;

            case "command":
                if (_nvim is null || string.IsNullOrEmpty(msg.data)) return;
                await SafeCommandAsync(msg.data!);
                return;

            case "title":
                if (!string.IsNullOrWhiteSpace(msg.title))
                {
                    Text = msg.title!;
                }
                return;

            case "colors":
                if (!string.IsNullOrWhiteSpace(msg.bg))
                {
                    TryApplyBackground(msg.bg!);
                }
                return;

            case "editorPointerDown":
                CloseAllMenuDropDowns();
                return;

            case "imeOff":
                ForceImeOff();
                return;
        }
    }

    private void EnsureNvimStartedAndAttach()
    {
        if (_nvim != null) return;

        try
        {
            _nvim = new NvimRpcClient(NvimExe);
            _nvim.Exited += _ => BeginInvoke(() => { try { Close(); } catch { } });
            _nvim.Redraw += events => OnRedraw(events);

            _nvim.Stderr += s =>
            {
                if (!string.IsNullOrWhiteSpace(s) && s.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                    BeginInvoke(() => ShowBootError("nvim stderr:\n" + s));
            };
            _nvim.Faulted += ex => BeginInvoke(() => ShowBootError("RPC error:\n" + ex));

            _ = AttachUiAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "nvim の起動に失敗しました:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OpenStartupArgsOnce()
    {
        if (Interlocked.Exchange(ref _startupArgsOpened, 1) != 0)
            return;

        if (_startupArgs == null || _startupArgs.Length == 0)
            return;

        foreach (var arg in _startupArgs)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            try
            {
                var full = Path.GetFullPath(arg);
                if (File.Exists(full))
                    OpenFileInNvim(full, OpenMode.NewTab);
            }
            catch
            {
            }
        }
    }

    private void ShowBootError(string message)
    {
        try
        {
            if (_web.CoreWebView2 is null) return;
            _web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new
            {
                type = "bootError",
                message
            }));
        }
        catch
        {
            try { MessageBox.Show(this, message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }
    }

    private async Task AttachUiAsync()
    {
        if (_nvim is null || _uiAttached) return;

        var opts = new Dictionary<string, object?>
        {
            ["rgb"] = true,
            ["ext_linegrid"] = true
        };

        try
        {
            await _nvim.CallAsync("nvim_ui_attach", (long)_lastCols, (long)_lastRows, opts);
            _uiAttached = true;

            BeginInvoke(() => { try { _redrawTimer.Start(); } catch { } });

            await SafeCommandAsync("silent set title");
            await SafeCommandAsync(@"silent set titlestring=%f\ -\ Nvim");
            await SafeCommandAsync(@"set guicursor=n-v-c:block,i-ci-ve:ver25,r-cr:hor20,o:hor50");
            await SafeCommandAsync(@"set mouse=a");

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await Task.Delay(150);
                    if (!IsDisposed)
                    {
                        _web.Focus();
                        ActiveControl = _web;
                    }
                }
                catch
                {
                }
            }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "nvim_ui_attach に失敗しました:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SendInputAsync(string input, bool isTermcode)
    {
        if (_nvim is null || string.IsNullOrEmpty(input)) return;

        try
        {
            // nvim_input は <BS>, <Left>, <CR> などの key notation をそのまま解釈できる。
            // replace_termcodes を挟むと byte[] / 文字列の扱い差で特殊キーが壊れやすいので、
            // ここでは常にそのまま渡す。
            await _nvim.CallAsync("nvim_input", input);
        }
        catch
        {
        }
    }

    private async Task SendMouseInputAsync(
    string button,
    string action,
    string modifiers,
    int grid,
    int row,
    int col)
    {
        if (_nvim is null) return;

        try
        {
            await _nvim.CallAsync(
                "nvim_input_mouse",
                button,
                action,
                modifiers ?? "",
                (long)grid,
                (long)row,
                (long)col);
        }
        catch
        {
        }
    }

    private async Task SafeCommandAsync(string ex)
    {
        if (_nvim is null) return;
        try { await _nvim.CallAsync("nvim_command", ex); } catch { }
    }

    private void OnRedraw(IReadOnlyList<object?> redrawParams)
    {
        _redrawQueue.Enqueue(redrawParams);

        if (!_firstRedrawSeen)
        {
            _firstRedrawSeen = true;
            ScheduleRestoreTreeStateAfterFirstRedraw();
        }
    }

    private void ScheduleRestoreTreeStateAfterFirstRedraw()
    {
        if (System.Threading.Interlocked.Exchange(ref _restoreScheduled, 1) != 0)
            return;

        _ = Task.Delay(800).ContinueWith(_ =>
        {
            if (IsDisposed) return;

            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await RestoreTreeStateAsync();
                }
                catch
                {
                }
            }));
        }, TaskScheduler.Default);
    }

    private void TryApplyBackground(string bgHex)
    {
        try
        {
            var c = ColorTranslator.FromHtml(bgHex);
            _web.DefaultBackgroundColor = c;
            BackColor = c;
            _tsc.ContentPanel.BackColor = c;
            _split.BackColor = c;
            _split.Panel2.BackColor = c;
        }
        catch { }
    }

    private async Task FlushRedrawAsync()
    {
        if (!_webReady) return;
        if (_redrawSerializeInFlight) return;
        if (_web.CoreWebView2 is null) return;

        if (!_redrawQueue.TryDequeue(out var first)) return;

        var raw = new List<IReadOnlyList<object?>>(96) { first };
        while (raw.Count < 80 && _redrawQueue.TryDequeue(out var item))
            raw.Add(item);

        _redrawSerializeInFlight = true;
        try
        {
            var payload = await Task.Run(() =>
            {
                var batches = new List<object?>(raw.Count);
                foreach (var r in raw)
                    batches.Add(NvimRpcClient.ToJsonable(r));

                return JsonSerializer.Serialize(new { type = "redrawBatch", batches });
            }).ConfigureAwait(true);

            if (IsDisposed) return;
            try { _web.CoreWebView2.PostWebMessageAsString(payload); } catch { }
        }
        finally
        {
            _redrawSerializeInFlight = false;
        }
    }

    // =========================================================
    // Menu
    // =========================================================
    private void BuildMenu()
    {
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");

        var open = new ToolStripMenuItem("開く(&O)...", null, (_, __) => MenuOpen())
        { ShortcutKeys = Keys.Control | Keys.O };

        var save = new ToolStripMenuItem("保存(&S)", null, (_, __) => MenuSave())
        { ShortcutKeys = Keys.Control | Keys.S };

        var saveAs = new ToolStripMenuItem("名前を付けて保存(&A)...", null, (_, __) => MenuSaveAs())
        { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };

        var close = new ToolStripMenuItem("閉じる(&C)", null, (_, __) => MenuCloseBuffer())
        { ShortcutKeys = Keys.Control | Keys.W };

        var exit = new ToolStripMenuItem("終了(&Q)", null, (_, __) => MenuExit())
        { ShortcutKeys = Keys.Alt | Keys.F4 };

        fileMenu.DropDownItems.Add(open);
        fileMenu.DropDownItems.Add(save);
        fileMenu.DropDownItems.Add(saveAs);
        fileMenu.DropDownItems.Add(close);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exit);

        var viewMenu = new ToolStripMenuItem("表示(&V)");
        var changeRoot = new ToolStripMenuItem("ツリーの更新(&U)...", null, (_, __) => ChangeTreeRootAndReload());
        var reload = new ToolStripMenuItem("ツリー再読み込み(&R)", null, (_, __) => ReloadTree())
        { ShortcutKeys = Keys.F5 };

        var openModeMenu = new ToolStripMenuItem("開き方");
        openModeMenu.DropDownItems.Add(MakeOpenModeItem("新しいタブ", OpenMode.NewTab, Keys.Control | Keys.Enter));
        openModeMenu.DropDownItems.Add(MakeOpenModeItem("現在の画面", OpenMode.Current, Keys.None));
        openModeMenu.DropDownItems.Add(MakeOpenModeItem("垂直分割", OpenMode.VSplit, Keys.None));
        openModeMenu.DropDownItems.Add(MakeOpenModeItem("水平分割", OpenMode.Split, Keys.None));

        viewMenu.DropDownItems.Add(openModeMenu);
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(changeRoot);
        viewMenu.DropDownItems.Add(reload);

        _menu.Items.Add(fileMenu);
        _menu.Items.Add(viewMenu);
    }

    private ToolStripMenuItem MakeOpenModeItem(string text, OpenMode mode, Keys shortcut)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = mode == _openMode };
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;

        item.Click += (_, __) =>
        {
            _openMode = mode;
            foreach (ToolStripMenuItem sib in item.GetCurrentParent()!.Items.OfType<ToolStripMenuItem>())
                sib.Checked = (sib == item);
        };
        return item;
    }

    private void MenuOpen()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "開く",
            Filter = "すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        OpenFileInNvim(ofd.FileName, OpenMode.NewTab);
    }

    private void MenuSave()
    {
        if (!_currentBufferHasFile) { MenuSaveAs(); return; }
        _ = SafeCommandAsync("write");
    }

    private void MenuSaveAs()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "名前を付けて保存",
            Filter = "すべてのファイル (*.*)|*.*",
            OverwritePrompt = true,
            FileName = _currentSuggestedFileName
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var pathLit = ToLuaStringLiteral(sfd.FileName);
        _ = SafeCommandAsync($"lua vim.cmd('silent keepalt saveas! ' .. vim.fn.fnameescape({pathLit}))");
    }

    private void MenuCloseBuffer()
        => _ = SafeCommandAsync("confirm bd");

    private void MenuExit()
    {
        var res = MessageBox.Show(this, "Neovimを終了しますか？", "終了",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (res != DialogResult.Yes) return;

        _ = SafeCommandAsync("confirm qall");
    }

    private void OpenFileInNvim(string fullPath, OpenMode mode)
    {
        if (!File.Exists(fullPath)) return;

        var pathLit = ToLuaStringLiteral(fullPath);

        string cmd = mode switch
        {
            //OpenMode.NewTab => "confirm tabedit",
            OpenMode.NewTab => "confirm tab drop",
            OpenMode.Current => "confirm edit",
            OpenMode.VSplit => "confirm vsplit",
            OpenMode.Split => "confirm split",
            _ => "confirm tabedit"
        };

        _ = SafeCommandAsync($"lua vim.cmd('{cmd} ' .. vim.fn.fnameescape({pathLit}))");
    }

    public void StartPipeServer(string pipeName)
    {
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => PipeServerLoopAsync(pipeName, _pipeCts.Token));
    }

    private async Task PipeServerLoopAsync(string pipeName, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var paths = new List<string>();

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                        paths.Add(line);
                }

                if (paths.Count == 0)
                    continue;

                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (WindowState == FormWindowState.Minimized)
                            WindowState = FormWindowState.Normal;

                        Show();
                        Activate();
                        BringToFront();

                        foreach (var p in paths)
                        {
                            if (File.Exists(p))
                                OpenFileInNvim(p, OpenMode.NewTab);
                        }

                        try
                        {
                            _web.Focus();
                            ActiveControl = _web;
                        }
                        catch
                        {
                        }
                    }
                    catch
                    {
                    }
                }));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // ループ継続
            }
        }
    }

    private static string ToLuaStringLiteral(string s)
    {
        if (!s.Contains("]]") && !s.Contains("\r") && !s.Contains("\n"))
            return $"[[{s}]]";

        var esc = s.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\r", "\\r")
                   .Replace("\n", "\\n");
        return $"\"{esc}\"";
    }

    private void CloseAllMenuDropDowns()
    {
        foreach (ToolStripItem item in _menu.Items)
            if (item is ToolStripMenuItem mi) mi.HideDropDown();
        _menu.Invalidate();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_SETOPENSTATUS = 0x0006;

    private void ForceImeOff()
    {
        try
        {
            IntPtr hwnd = GetFocus();
            if (hwnd == IntPtr.Zero)
                hwnd = _web.Handle;

            if (hwnd == IntPtr.Zero)
                return;

            // まず通常の IMM コンテキスト取得を試す
            var hImc = ImmGetContext(hwnd);
            if (hImc != IntPtr.Zero)
            {
                try
                {
                    ImmSetOpenStatus(hImc, false);
                    return;
                }
                finally
                {
                    ImmReleaseContext(hwnd, hImc);
                }
            }

            // IMM コンテキストが取れない場合は、既定 IME ウィンドウに対して OPENSTATUS を閉じる
            var imeWnd = ImmGetDefaultIMEWnd(hwnd);
            if (imeWnd != IntPtr.Zero)
            {
                SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, IntPtr.Zero);
            }
        }
        catch
        {
        }
    }

    // =========================================================
    // Explorer (TreeView)
    // =========================================================
    private static Bitmap GetShellIconBitmap(string path, bool isDirectory, bool openFolder = false)
    {
        uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
        if (isDirectory && openFolder) flags |= SHGFI_OPENICON;

        SHFILEINFO shfi;
        var res = SHGetFileInfo(path, attrs, out shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return SystemIcons.Application.ToBitmap();

        try
        {
            using var icon = Icon.FromHandle(shfi.hIcon);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private string EnsureFileIconKey(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext)) ext = "__noext__";

        if (_fileIconKeyByExt.TryGetValue(ext, out var existing))
            return existing;

        var key = "__file_" + (++_fileIconSeq).ToString();
        _icons.Images.Add(key, GetShellIconBitmap(fullPath, isDirectory: false));
        _fileIconKeyByExt[ext] = key;
        return key;
    }

    private void SetupExplorerIcons()
    {
        _tree.ImageList = _icons;
        _icons.Images.Clear();
        _fileIconKeyByExt.Clear();

        _icons.Images.Add(IconKeyFolder, GetShellIconBitmap("folder", isDirectory: true, openFolder: false));
        _icons.Images.Add(IconKeyFolderOpen, GetShellIconBitmap("folder", isDirectory: true, openFolder: true));
    }

    private void SetupExplorerTree()
    {
        _tree.BeforeExpand += async (_, e) =>
        {
            if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is FsTag t && t.FullPath == DummyTag.FullPath)
                await PopulateDirectoryNodeAsync(e.Node);
        };

        _tree.AfterExpand += (_, e) =>
        {
            if (e.Node?.Tag is FsTag tag && tag.IsDirectory)
            {
                e.Node.ImageKey = IconKeyFolderOpen;
                e.Node.SelectedImageKey = IconKeyFolderOpen;
            }
        };

        _tree.AfterCollapse += (_, e) =>
        {
            if (e.Node?.Tag is FsTag tag && tag.IsDirectory)
            {
                e.Node.ImageKey = IconKeyFolder;
                e.Node.SelectedImageKey = IconKeyFolder;
            }
        };

        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is not FsTag tag) return;

            if (!tag.IsDirectory)
                OpenFileInNvim(tag.FullPath, OpenMode.NewTab);
        };

        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            var node = _tree.SelectedNode;
            if (node?.Tag is not FsTag tag) return;

            e.Handled = true;
            e.SuppressKeyPress = true;

            if (tag.IsDirectory) node.Toggle();
            else OpenFileInNvim(tag.FullPath, _openMode);
        };

        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _tree.SelectedNode = e.Node;
                _treeMenu.Show(_tree, e.Location);
            }
        };

        ReloadTree(expandRoot: true);
    }

    private void SetupExplorerContextMenu()
    {
        _treeMenu.Items.Clear();
        _treeMenu.Items.Add(new ToolStripMenuItem("ツリーの更新(&U)...", null, (_, __) => ChangeTreeRootAndReload()));
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(new ToolStripMenuItem("新しいタブで開く", null, (_, __) => OpenSelectedInMode(OpenMode.NewTab)));
        _treeMenu.Items.Add(new ToolStripMenuItem("現在の画面で開く", null, (_, __) => OpenSelectedInMode(OpenMode.Current)));
        _treeMenu.Items.Add(new ToolStripMenuItem("垂直分割で開く", null, (_, __) => OpenSelectedInMode(OpenMode.VSplit)));
        _treeMenu.Items.Add(new ToolStripMenuItem("水平分割で開く", null, (_, __) => OpenSelectedInMode(OpenMode.Split)));
        _treeMenu.Items.Add(new ToolStripSeparator());
        _treeMenu.Items.Add(new ToolStripMenuItem("再読み込み", null, async (_, __) => await ReloadSelectedNodeAsync()));
    }

    private void OpenSelectedInMode(OpenMode mode)
    {
        var node = _tree.SelectedNode;
        if (node?.Tag is not FsTag tag) return;

        if (tag.IsDirectory) node.Toggle();
        else OpenFileInNvim(tag.FullPath, mode);
    }

    private async Task ReloadSelectedNodeAsync()
    {
        TreeNode? node = _tree.SelectedNode;
        if (node?.Tag is FsTag tag && !tag.IsDirectory)
            node = node.Parent;

        if (node?.Tag is not FsTag dirTag || !dirTag.IsDirectory)
        {
            ReloadTree(expandRoot: true);
            return;
        }

        var parent = node.Parent;
        int index = parent is null ? _tree.Nodes.IndexOf(node) : parent.Nodes.IndexOf(node);
        if (index < 0)
        {
            ReloadTree(expandRoot: true);
            return;
        }

        bool wasExpanded = node.IsExpanded;
        bool wasSelected = (_tree.SelectedNode == node);

        var replacement = CreateDirectoryNode(dirTag.FullPath);

        _tree.BeginUpdate();
        try
        {
            if (parent is null)
            {
                _tree.Nodes.RemoveAt(index);
                _tree.Nodes.Insert(index, replacement);
            }
            else
            {
                parent.Nodes.RemoveAt(index);
                parent.Nodes.Insert(index, replacement);
            }

            if (wasSelected)
                _tree.SelectedNode = replacement;
        }
        finally
        {
            _tree.EndUpdate();
        }

        if (wasExpanded)
        {
            await PopulateDirectoryNodeAsync(replacement);
            replacement.Expand();
        }

        _tree.Refresh();
    }

    private void ChangeTreeRootAndReload()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "ツリーのルートフォルダを選択してください",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_treeRootPath) ? _treeRootPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(dlg.SelectedPath))
        {
            MessageBox.Show(this, "選択したフォルダが存在しません。", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _treeRootPath = dlg.SelectedPath;
        ReloadTree(expandRoot: true);
    }

    private void ReloadTree(bool expandRoot = false)
    {
        LoadExplorerRoot();
        if (expandRoot && _tree.Nodes.Count > 0)
        {
            _tree.SelectedNode = _tree.Nodes[0];
            _tree.Nodes[0].Expand();
        }

        _tree.Invalidate();
        _tree.Update();
    }

    private TreeNode CreateDirectoryNode(string path)
    {
        var node = new TreeNode(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            Tag = new FsTag(path, true),
            ImageKey = IconKeyFolder,
            SelectedImageKey = IconKeyFolder,
            ToolTipText = path
        };

        if (string.IsNullOrEmpty(node.Text))
            node.Text = path;

        if (DirectoryMayHaveChildren(path))
            node.Nodes.Add(new TreeNode("...") { Tag = DummyTag });

        return node;
    }

    private void LoadExplorerRoot()
    {
        _loadingDirs.Clear();

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();

            var rootPath = _treeRootPath;
            var rootNode = new TreeNode(rootPath)
            {
                Tag = new FsTag(rootPath, true),
                ImageKey = IconKeyFolder,
                SelectedImageKey = IconKeyFolder,
                ToolTipText = rootPath
            };

            if (DirectoryMayHaveChildren(rootPath))
                rootNode.Nodes.Add(new TreeNode("...") { Tag = DummyTag });

            _tree.Nodes.Add(rootNode);
            rootNode.ImageKey = IconKeyFolder;
            rootNode.SelectedImageKey = IconKeyFolder;
        }
        finally
        {
            _tree.EndUpdate();
        }

        _tree.Refresh();
    }

    private async Task PopulateDirectoryNodeAsync(TreeNode dirNode)
    {
        if (InvokeRequired)
        {
            var tcs = new TaskCompletionSource<bool>();
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await PopulateDirectoryNodeAsync(dirNode);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
            await tcs.Task;
            return;
        }

        if (dirNode.Tag is not FsTag tag || !tag.IsDirectory) return;

        var path = tag.FullPath;
        if (!_loadingDirs.TryAdd(path, 0)) return;

        dirNode.Nodes.Clear();
        dirNode.Nodes.Add(new TreeNode("Loading...") { Tag = DummyTag });

        try
        {
            var listing = await Task.Run(() => EnumerateDirectory(path));
            if (IsDisposed) return;

            BeginInvoke(() =>
            {
                try
                {
                    _tree.BeginUpdate();
                    try
                    {
                        dirNode.Nodes.Clear();

                        foreach (var d in listing.Directories)
                        {
                            var name = Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            if (string.IsNullOrEmpty(name)) name = d;

                            var n = new TreeNode(name)
                            {
                                Tag = new FsTag(d, true),
                                ImageKey = IconKeyFolder,
                                SelectedImageKey = IconKeyFolder,
                                ToolTipText = d
                            };

                            if (DirectoryMayHaveChildren(d))
                                n.Nodes.Add(new TreeNode("...") { Tag = DummyTag });

                            dirNode.Nodes.Add(n);
                        }

                        foreach (var f in listing.Files)
                        {
                            var name = Path.GetFileName(f);
                            var fileIconKey = EnsureFileIconKey(f);

                            dirNode.Nodes.Add(new TreeNode(name)
                            {
                                Tag = new FsTag(f, false),
                                ImageKey = fileIconKey,
                                SelectedImageKey = fileIconKey,
                                ToolTipText = f
                            });
                        }
                    }
                    finally
                    {
                        _tree.EndUpdate();
                    }

                    _tree.Refresh();
                }
                catch { }
            });
        }
        finally
        {
            _loadingDirs.TryRemove(path, out _);
        }
    }

    private sealed class DirListing
    {
        public List<string> Directories { get; } = new();
        public List<string> Files { get; } = new();
    }

    private static DirListing EnumerateDirectory(string path)
    {
        var res = new DirListing();
        try { res.Directories.AddRange(Directory.EnumerateDirectories(path)); } catch { }
        try { res.Files.AddRange(Directory.EnumerateFiles(path)); } catch { }

        res.Directories.Sort(StringComparer.OrdinalIgnoreCase);
        res.Files.Sort(StringComparer.OrdinalIgnoreCase);
        return res;
    }

    private static bool DirectoryMayHaveChildren(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Take(1).Any(); }
        catch { return false; }
    }

    // =========================================================
    // Persist tree state (ExpandedDirs <= 100)
    // =========================================================
    private sealed class TreePersistState
    {
        public string? RootPath { get; set; }
        public List<string> ExpandedDirs { get; set; } = new();
        public string? SelectedPath { get; set; }
        public string? TopPath { get; set; }
    }

    private static string? GetNodePath(TreeNode? node)
        => (node?.Tag as FsTag)?.FullPath;

    private IEnumerable<TreeNode> WalkNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var c in WalkNodes(n.Nodes))
                yield return c;
        }
    }

    private void EnsureStateDirExists()
    {
        var dir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static int PathDepth(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        int depth = 0;
        foreach (var ch in path)
            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar) depth++;
        return depth;
    }

    private static IEnumerable<string> EnumerateAncestorDirs(string root, string? pathOrFile)
    {
        if (string.IsNullOrWhiteSpace(pathOrFile)) yield break;

        var p = pathOrFile!;
        if (File.Exists(p))
            p = Path.GetDirectoryName(p) ?? p;

        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) yield break;

        var cur = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stack = new Stack<string>();
        while (!string.IsNullOrEmpty(cur))
        {
            stack.Push(cur);
            if (string.Equals(cur, root, StringComparison.OrdinalIgnoreCase)) break;
            cur = Path.GetDirectoryName(cur) ?? "";
        }

        while (stack.Count > 0)
            yield return stack.Pop();
    }

    private void SaveTreeState()
    {
        try
        {
            var rootPath = _treeRootPath;

            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in WalkNodes(_tree.Nodes))
            {
                if (n.Tag is FsTag tag && tag.IsDirectory && n.IsExpanded)
                    expanded.Add(tag.FullPath);
            }

            var selectedPath = GetNodePath(_tree.SelectedNode);
            var topPath = GetNodePath(_tree.TopNode);

            var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPath };
            foreach (var a in EnumerateAncestorDirs(rootPath, selectedPath)) pinned.Add(a);
            foreach (var a in EnumerateAncestorDirs(rootPath, topPath)) pinned.Add(a);

            var pinnedList = pinned
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int remain = Math.Max(0, MaxExpandedDirsToSave - pinnedList.Count);

            var others = expanded
                .Where(p => !pinned.Contains(p))
                .OrderByDescending(PathDepth)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(remain)
                .ToList();

            var finalExpanded = pinnedList
                .Concat(others)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxExpandedDirsToSave)
                .ToList();

            var state = new TreePersistState
            {
                RootPath = rootPath,
                ExpandedDirs = finalExpanded,
                SelectedPath = selectedPath,
                TopPath = topPath
            };

            EnsureStateDirExists();
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private async Task RestoreTreeStateAsync()
    {
        if (_restoreInProgress) return;
        _restoreInProgress = true;

        try
        {
            TreePersistState? state = null;
            try
            {
                if (File.Exists(_stateFilePath))
                    state = JsonSerializer.Deserialize<TreePersistState>(File.ReadAllText(_stateFilePath));
            }
            catch { state = null; }

            if (!string.IsNullOrWhiteSpace(state?.RootPath) && Directory.Exists(state.RootPath))
                _treeRootPath = state.RootPath!;
            else
                _treeRootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            LoadExplorerRoot();
            if (_tree.Nodes.Count == 0) return;

            var rootNode = _tree.Nodes[0];
            await EnsureExpandedAsync(rootNode);

            var expanded = (state?.ExpandedDirs ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PathDepth)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in expanded)
                await EnsurePathExpandedAsync(p);

            TreeNode? selected = null;
            if (!string.IsNullOrWhiteSpace(state?.SelectedPath))
                selected = FindNodeByPath(state!.SelectedPath!);

            if (selected != null)
            {
                _tree.SelectedNode = selected;
                selected.EnsureVisible();
            }

            if (!string.IsNullOrWhiteSpace(state?.TopPath))
            {
                var top = FindNodeByPath(state!.TopPath!);
                if (top != null) _tree.TopNode = top;
                else if (selected != null) _tree.TopNode = selected;
            }
            else if (selected != null)
            {
                _tree.TopNode = selected;
            }
        }
        finally
        {
            _restoreInProgress = false;
        }
    }

    private async Task EnsureExpandedAsync(TreeNode node)
    {
        if (node.Nodes.Count == 1 && node.Nodes[0].Tag is FsTag t && t.FullPath == DummyTag.FullPath)
            await PopulateDirectoryNodeAsync(node);

        if (!node.IsExpanded) node.Expand();
    }

    private async Task EnsurePathExpandedAsync(string fullPath)
    {
        if (string.IsNullOrEmpty(_treeRootPath)) return;
        if (!fullPath.StartsWith(_treeRootPath, StringComparison.OrdinalIgnoreCase)) return;

        if (_tree.Nodes.Count == 0) return;
        var cur = _tree.Nodes[0];
        await EnsureExpandedAsync(cur);

        var target = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (true)
        {
            await EnsureExpandedAsync(cur);

            TreeNode? next = null;
            foreach (TreeNode child in cur.Nodes)
            {
                if (child.Tag is FsTag tag && tag.IsDirectory)
                {
                    var childPath = tag.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(childPath, target, StringComparison.OrdinalIgnoreCase)
                        || target.StartsWith(childPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || target.StartsWith(childPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        next = child;
                        break;
                    }
                }
            }

            if (next == null) break;

            cur = next;

            var curPath = (cur.Tag as FsTag)?.FullPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(curPath) && string.Equals(curPath, target, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureExpandedAsync(cur);
                break;
            }
        }
    }

    private TreeNode? FindNodeByPath(string fullPath)
    {
        foreach (var n in WalkNodes(_tree.Nodes))
            if (n.Tag is FsTag tag && string.Equals(tag.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return n;
        return null;
    }

    // =========================================================
    // Web message model
    // =========================================================
    private sealed class WebMsg
    {
        public string? type { get; set; }
        public string? data { get; set; }
        public string? title { get; set; }
        public int cols { get; set; }
        public int rows { get; set; }
        public bool? termcode { get; set; }
        public string? bg { get; set; }
        public string? button { get; set; }
        public string? action { get; set; }
        public string? modifiers { get; set; }
        public int grid { get; set; }
        public int row { get; set; }
        public int col { get; set; }
    }
}