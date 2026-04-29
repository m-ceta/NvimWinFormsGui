# Linux UI Parity And Logging

## Windows UI baseline

Read-only reference:

- `NvimWinFormsGui/MainForm.cs`
- `NvimWinFormsGui/MainForm.resx`

### Main menu

- Top-level menus are `ファイル(&F)` and `表示(&V)`.
- `ファイル(&F)` order:
  - `開く(&O)...`
  - `保存(&S)`
  - `名前を付けて保存(&A)...`
  - `閉じる(&C)`
  - separator
  - `終了(&Q)`
- `表示(&V)` order:
  - `開き方`
  - separator
  - `ツリーの更新(&U)...`
  - `ツリー再読み込み(&R)`
  - separator
  - Windows-only debug toggle
- Windows shortcuts confirmed in `MainForm.cs`:
  - `Ctrl+O`
  - `Ctrl+S`
  - `Ctrl+Shift+S`
  - `Ctrl+W`
  - `Alt+F4`
  - `F5`
  - `Ctrl+Enter` for `新しいタブ`
- Windows returns focus to the editor WebView after menu-driven open/save/diff operations.

### Folder tree

- Split layout is left tree + splitter + right editor.
- Initial splitter distance is `280`.
- Minimum widths are `220` for tree pane and `400` for editor pane.
- Root defaults to the user home directory.
- Tree order is directories first, then files, both case-insensitive ascending.
- Double click on a file opens it in a new tab.
- `Enter` toggles a directory or opens a file with the current open mode.
- Right click selects the clicked node first, then opens the tree context menu.
- Expanded folders switch to the open-folder icon; collapsed folders switch back.
- Windows keeps editor focus behavior predictable after open/diff operations.

### Context menu

- Order:
  - `ツリーの更新(&U)...`
  - separator
  - `新しいタブで開く`
  - `現在の画面で開く`
  - `垂直分割で開く`
  - `水平分割で開く`
  - separator
  - `現在のバッファと比較`
  - separator
  - `再読み込み`
- File node:
  - open variants enabled
  - compare enabled
  - reload enabled
- Directory node:
  - open variants effectively toggle/act on the selected directory
  - compare disabled
  - reload enabled
- Blank area:
  - tree root change stays relevant
  - file-scoped actions should stay disabled

## Linux logging

### Enable with environment variables

```powershell
$env:NVIM_GUI_LOG='1'
$env:NVIM_GUI_LOG_LEVEL='Debug'
$env:NVIM_GUI_LOG_CATEGORIES='Layout,Resize,Cmdline,Mouse,Keyboard,TextInput,FloatingGrid,RedrawEvent,FolderTree,MainMenu,ContextMenu'
$env:NVIM_GUI_LOG_EVENTS='1'
dotnet run --project .\NvimGuiLinux.Avalonia\
```

### Enable with startup args

```powershell
dotnet run --project .\NvimGuiLinux.Avalonia\ -- --gui-log --gui-log-level=Debug --gui-log-categories=Layout,Resize,Cmdline,Mouse,Keyboard,TextInput,FloatingGrid,RedrawEvent,FolderTree,MainMenu,ContextMenu --gui-log-events
```

### Output

- Standard output
- `logs/nvim-gui-linux-YYYYMMDD-HHMMSS.log`

### Categories

- `Layout`
- `Resize`
- `Render`
- `RedrawEvent`
- `Cmdline`
- `Message`
- `PopupMenu`
- `FloatingGrid`
- `Mouse`
- `Keyboard`
- `TextInput`
- `Focus`
- `FolderTree`
- `MainMenu`
- `ContextMenu`
- `Performance`

## Manual verification scenarios

### Statusline and resize

1. Start with logging enabled.
2. Check startup layout logs.
3. Resize the window.
4. Toggle folder tree visibility.
5. Drag the splitter.
6. Confirm `LineGridControl` resize logs show the new `cols` and `rows`.

### Cmdline input

1. Press `:`.
2. Press `/`.
3. Press `?`.
4. Confirm logs for:
   - `KeyDown`
   - `TextInput`
   - `EditorController.InputAsync`
   - `cmdline_show`

### Floating mouse hit test

1. Open an LSP hover or another floating window.
2. Click inside the floating window.
3. Confirm mouse logs include:
   - raw position
   - matched grid id
   - matched grid rect
   - local row
   - local col

### Folder tree and menus

1. Compare Linux menu structure against the Windows baseline above.
2. Toggle folder tree visibility.
3. Resize the folder tree with the splitter.
4. Expand a directory.
5. Double click a file.
6. Press `Enter` on a file.
7. Right click file node, directory node, and blank area.
8. After each menu or context-menu action, click back into the editor and confirm `:` works in one keypress.
