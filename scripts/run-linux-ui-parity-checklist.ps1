param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$SkipBuild,

    [switch]$LaunchOnly,

    [switch]$DriveEditorScenarios,

    [string]$Project = "NvimGuiLinux.Avalonia"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixtureDir = Join-Path $repoRoot ".artifacts\parity-fixtures"
$fixturePath = Join-Path $fixtureDir "linux-parity-fixture.txt"
$logDir = Join-Path $repoRoot "logs"

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function New-ParityFixture {
    param([string]$Path)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Linux UI parity fixture")
    $lines.Add("# This file is generated. Use it for wrap, popupmenu, messages, floating-window, and minimap checks.")
    $lines.Add("")
    $lines.Add("PARITY_COMPLETION_TOKEN_ALPHA PARITY_COMPLETION_TOKEN_BETA PARITY_COMPLETION_TOKEN_GAMMA")
    $lines.Add("PARITY_COMPLETION_TOKEN_ALPHA_EXTENDED PARITY_COMPLETION_TOKEN_BETA_EXTENDED PARITY_COMPLETION_TOKEN_GAMMA_EXTENDED")
    $lines.Add("")
    $lines.Add("The next block contains long wrapped lines.")

    for ($i = 1; $i -le 40; $i++) {
        $lines.Add(("WRAP-{0:D3} " -f $i) + ("lorem_ipsum_segment_{0:D3} " -f $i) * 12)
    }

    $lines.Add("")
    $lines.Add("The next block contains dense completion tokens.")
    for ($i = 1; $i -le 80; $i++) {
        $lines.Add(("TOKEN-{0:D3} PARITY_COMPLETION_TOKEN_{0:D3} sample_text_{0:D3}" -f $i))
    }

    $lines.Add("")
    $lines.Add("The next block contains many lines for minimap and scrolling.")
    for ($i = 1; $i -le 717; $i++) {
        $lines.Add(("LINE-{0:D3} quick_brown_fox_jumps_over_the_lazy_dog minimap_probe_{0:D3}" -f $i))
    }

    Set-Content -Path $Path -Value $lines -Encoding UTF8
}

function Start-LinuxGui {
    param(
        [string]$RepoRoot,
        [string]$ProjectName,
        [string]$FilePath,
        [string]$ConfigurationName
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.ArgumentList.Add("run")
    $psi.ArgumentList.Add("--project")
    $psi.ArgumentList.Add($ProjectName)
    $psi.ArgumentList.Add("-c")
    $psi.ArgumentList.Add($ConfigurationName)
    $psi.ArgumentList.Add("--")
    $psi.ArgumentList.Add($FilePath)
    $psi.ArgumentList.Add("--line")
    $psi.ArgumentList.Add("1")
    $psi.Environment["NVIM_GUI_LOG"] = "1"
    $psi.Environment["NVIM_GUI_LOG_LEVEL"] = "Debug"
    $psi.Environment["NVIM_GUI_LOG_CATEGORIES"] = "Layout,Resize,Render,RedrawEvent,Cmdline,Message,PopupMenu,FloatingGrid,Mouse,Keyboard,TextInput,Focus,FolderTree,MainMenu,ContextMenu,Performance"
    $psi.Environment["NVIM_GUI_LOG_EVENTS"] = "1"

    return [System.Diagnostics.Process]::Start($psi)
}

function Wait-ForMainWindow {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return $true
        }
        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Activate-ProcessWindow {
    param([System.Diagnostics.Process]$Process)

    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.Interaction]::AppActivate($Process.Id) | Out-Null
    Start-Sleep -Milliseconds 250
}

function Send-Keys {
    param([string]$Keys)

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds 200
}

function Send-Command {
    param([string]$CommandText)

    Set-Clipboard -Value $CommandText
    Send-Keys ":"
    Send-Keys "^v"
    Send-Keys "{ENTER}"
    Start-Sleep -Milliseconds 400
}

function Drive-ScenarioSequence {
    param([System.Diagnostics.Process]$Process)

    Activate-ProcessWindow -Process $Process

    Write-Host "Driving editor scenarios..." -ForegroundColor Yellow

    # Normal editing
    Send-Keys "gg"
    Send-Keys "{DOWN 3}"
    Send-Keys "iPARITY_EDIT_MARKER{ESC}"

    # Wrapped lines
    Send-Command "set wrap"
    Send-Command "25"
    Send-Keys "{PGDN}{PGDN}{PGUP}"

    # Cmdline
    Send-Keys ":"
    Send-Keys "set number"
    Send-Keys "{ENTER}"
    Send-Keys "/"
    Send-Keys "PARITY_COMPLETION_TOKEN"
    Send-Keys "{ENTER}"
    Send-Keys "n"

    # Popup menu using builtin keyword completion
    Send-Command "70"
    Send-Keys "A "
    Send-Keys "^x^n"
    Send-Keys "{DOWN}{ENTER}{ESC}"

    # Messages
    Send-Command "echo 'PARITY_MESSAGE_OK'"
    Send-Command "echoerr 'PARITY_MESSAGE_ERROR'"

    # Floating window
    $floatCmd = "lua local b=vim.api.nvim_create_buf(false,true); vim.api.nvim_buf_set_lines(b,0,-1,false,{'PARITY FLOAT','line 2','line 3'}); vim.api.nvim_open_win(b,false,{relative='editor',row=2,col=10,width=30,height=4,style='minimal',border='rounded'})"
    Send-Command $floatCmd

    # Minimap display / scrolling
    Send-Command "300"
    Send-Keys "{PGDN}{PGDN}{PGDN}{PGUP}"
}

function Write-ManualChecklist {
    Write-Section "Manual follow-up"
    Write-Host "Run the remaining visual/manual checks after the window is up:"
    Write-Host "- Startup layout"
    Write-Host "- Popup placement and styling"
    Write-Host "- Cmdline wrap and scroll behavior"
    Write-Host "- Message positioning and confirm-style prompts"
    Write-Host "- Floating window anchor, border, and shadow"
    Write-Host "- Folder tree behavior and context menu"
    Write-Host "- Minimap click and drag behavior"
}

Write-Section "Prepare fixtures"
New-ParityFixture -Path $fixturePath
Write-Host "Fixture: $fixturePath"

if (-not $SkipBuild) {
    Write-Section "Build"
    & dotnet build "$repoRoot\NvimWinFormsGui.sln" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

Write-Section "Launch"
$process = Start-LinuxGui -RepoRoot $repoRoot -ProjectName $Project -FilePath $fixturePath -ConfigurationName $Configuration
if (-not $process) {
    throw "Failed to start the Linux GUI process."
}

Write-Host "Process ID: $($process.Id)"
Write-Host "Waiting for window..."
if (-not (Wait-ForMainWindow -Process $process)) {
    throw "Timed out waiting for the GUI window."
}

Write-Host "Window is ready."
Write-Host "Log directory: $logDir"

if (-not $LaunchOnly -and $DriveEditorScenarios) {
    Write-Section "Automated editor scenarios"
    Drive-ScenarioSequence -Process $process
}

Write-ManualChecklist

Write-Section "Notes"
Write-Host "This runner automates fixture generation, launch, logging, and editor-side scenarios."
Write-Host "It does not yet fully automate:"
Write-Host "- Folder tree pointer scenarios"
Write-Host "- Pixel-perfect visual comparison"
Write-Host "- Minimap mouse click/drag verification"
Write-Host "- Cross-platform screenshot diffing"
