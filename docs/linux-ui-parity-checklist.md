# Linux UI Parity Checklist

## Purpose

Use this checklist to compare the Linux UI against the Windows UI baseline and confirm parity of layout, display, and interaction.

Read-only Windows baseline:

- `NvimWinFormsGui/MainForm.cs`
- `NvimWinFormsGui/wwwroot/index.html`

Linux implementation under test:

- `NvimGuiLinux.Avalonia/Views/MainWindow.axaml`
- `NvimGuiLinux.Avalonia/Views/MainWindow.axaml.cs`
- `NvimGuiLinux.Avalonia/Controls/*`

## Checklist

### 1. Startup layout

Verification:

1. Start Windows and Linux builds with the same font and similar window size.
2. Compare title bar text, menu bar presence, folder tree visibility, splitter position, editor area size, and overall background/foreground balance.

Expected result:

- Both builds show the same top-level layout structure.
- Folder tree is visible on the left by default.
- Initial splitter position and overall proportions are visually aligned.
- Editor area fills the right pane without extra gaps.

### 2. Normal editing

Verification:

1. Open the same file in both builds.
2. Move the cursor with arrow keys and type plain text.
3. Confirm redraw, cursor position, and inserted text stay aligned.

Expected result:

- Text position, cursor shape, and cursor movement look the same.
- No extra row or column offsets appear in Linux.
- Inserted text lands in the same logical and visual position.

### 3. Wrapped lines

Verification:

1. Open a file with long lines and enable wrapping.
2. Scroll through wrapped sections.
3. Move the cursor across wrapped boundaries.

Expected result:

- Wrapped layout matches Windows closely.
- Cursor movement across wrapped display rows does not drift.
- No visible row collapse, overlap, or phantom spacing appears.

### 4. Popup menu

Verification:

1. Trigger completion so `popupmenu` is shown.
2. Compare popup position, selected item highlight, border, shadow, scrollbar, and item alignment.
3. Change selection with keyboard and mouse.

Expected result:

- Popup appears in the same relative place as Windows.
- Selected row, colors, and spacing match closely.
- Mouse and keyboard selection change the same item as Windows.

### 5. Cmdline

Verification:

1. Open `:`, `/`, and `?` command lines.
2. Enter enough text to force horizontal cursor travel and prompt wrapping cases.
3. Check cursor visibility, prompt placement, and scrolling behavior.

Expected result:

- Cmdline appears at the same bottom position as Windows.
- Cursor remains visible while typing.
- Prompt-only wrap and long-text scrolling behave the same as Windows.

### 6. Messages

Verification:

1. Trigger normal messages, warnings, and confirm-style prompts.
2. Open message history when applicable.
3. Compare message placement, stacking, trimming, and status line coexistence.

Expected result:

- Messages appear in the same vertical region as Windows.
- Confirm-style messages remain readable and interactive.
- History and transient message behavior match Windows expectations.

### 7. Floating windows

Verification:

1. Trigger LSP hover, diagnostics, or another floating window.
2. Compare anchor position, shadow, clipping, and cursor behavior.
3. Click inside and around the floating window.

Expected result:

- Floating windows are anchored and layered the same way as Windows.
- Shadow and border presence are consistent.
- Mouse hit testing resolves to the floating grid when expected.

### 8. Folder tree

Verification:

1. Expand and collapse directories.
2. Double click a file.
3. Press `Enter` on a file and on a directory.
4. Right click file node, directory node, and blank area.

Expected result:

- Tree ordering, open behavior, and context-menu enablement match Windows.
- Splitter resizing feels the same.
- Focus can return to the editor predictably after tree actions.

### 9. Minimap display

Verification:

1. Open a large file.
2. Compare Minimap visibility, right-edge overlay position, viewport frame, cursor line marker, colors, and wrapped-line mapping.
3. Scroll in the main editor and watch Minimap updates.

Expected result:

- Minimap is overlaid at the right edge of the editor area.
- Buffer-wide content is visible, not just the current viewport.
- Viewport frame and cursor marker track Windows behavior.
- Wrapped lines do not introduce visible layout drift.

### 10. Minimap interaction

Verification:

1. Click several positions in Minimap.
2. Drag through Minimap from top to bottom.
3. Repeat on wrapped-line regions and near file start/end.

Expected result:

- Clicking scrolls the target position into the editor in the same way as Windows.
- Dragging produces continuous scrolling without dead zones.
- Interaction remains stable near wrapped sections and file boundaries.

## Pass criteria

- A checklist item passes only when Linux matches Windows in both visible result and interaction outcome.
- Small rendering differences are acceptable only if they do not change layout, hit testing, or user-perceived behavior.
- Any remaining mismatch should be recorded with:
  - scenario name
  - Linux result
  - Windows result
  - suspected owner: shared model, Linux layout, Linux overlay, or Linux input
