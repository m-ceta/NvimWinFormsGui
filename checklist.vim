" Plugin-free manual verification helpers for NvimGuiLinux.Avalonia.
" Usage:
"   :source /absolute/path/to/checklist.vim
" Then run:
"   :ChecklistHelp

if exists('g:loaded_nvim_gui_linux_checklist')
  finish
endif
let g:loaded_nvim_gui_linux_checklist = 1
let g:checklist_log = []

function! s:Log(msg) abort
  let l:line = '[' . strftime('%H:%M:%S') . '] ' . a:msg
  call add(g:checklist_log, l:line)
  echom l:line
endfunction

function! s:ChecklistScratch(lines, opts) abort
  let l:buf = nvim_create_buf(v:false, v:true)
  call nvim_buf_set_lines(l:buf, 0, -1, v:false, a:lines)
  call nvim_open_win(l:buf, v:true, a:opts)
  return l:buf
endfunction

function! ChecklistFloatingBasic() abort
  call <SID>Log('floating basic: open single-border floating window')
  call <SID>ChecklistScratch([
        \ 'floating test',
        \ 'move/close this window',
        \ 'confirm border and no ghosting',
        \ 'close with :q or :bd!'
        \ ], {
        \ 'relative': 'editor',
        \ 'row': 3,
        \ 'col': 10,
        \ 'width': 34,
        \ 'height': 6,
        \ 'style': 'minimal',
        \ 'border': 'single'
        \ })
endfunction

function! ChecklistFloatingAlt() abort
  call <SID>Log('floating alt: open rounded-border floating window')
  call <SID>ChecklistScratch([
        \ 'floating alt test',
        \ 'different anchor/position',
        \ 'check overlap and redraw'
        \ ], {
        \ 'relative': 'editor',
        \ 'row': 10,
        \ 'col': 30,
        \ 'width': 28,
        \ 'height': 5,
        \ 'style': 'minimal',
        \ 'border': 'rounded'
        \ })
endfunction

function! ChecklistHighlightReset() abort
  call <SID>Log('highlight reset')
  silent! call clearmatches()
  silent! highlight clear TestBold
  silent! highlight clear TestItalic
  silent! highlight clear TestUnderline
  silent! highlight clear TestCurl
  silent! highlight clear TestStrike
  silent! highlight clear TestReverse
  silent! highlight clear TestBlend
endfunction

function! ChecklistHighlightSetup() abort
  call <SID>Log('highlight setup: bold/italic/underline/undercurl/strike/reverse/blend')
  call ChecklistHighlightReset()
  enew
  setlocal buftype=
  setlocal bufhidden=
  setlocal noswapfile
  call setline(1, [
        \ 'Bold sample',
        \ 'Italic sample',
        \ 'Underline sample',
        \ 'Undercurl sample',
        \ 'Strikethrough sample',
        \ 'Reverse sample',
        \ 'Blend sample'
        \ ])

  highlight TestBold gui=bold cterm=bold
  highlight TestItalic gui=italic cterm=italic
  highlight TestUnderline gui=underline cterm=underline
  highlight TestCurl gui=undercurl guisp=#ff0000 cterm=underline
  highlight TestStrike gui=strikethrough cterm=strikethrough
  highlight TestReverse guifg=#ffffff guibg=#005f87 gui=reverse cterm=reverse
  highlight TestBlend guifg=#ffffff guibg=#5f005f blend=40

  call matchadd('TestBold', '\%1l.*')
  call matchadd('TestItalic', '\%2l.*')
  call matchadd('TestUnderline', '\%3l.*')
  call matchadd('TestCurl', '\%4l.*')
  call matchadd('TestStrike', '\%5l.*')
  call matchadd('TestReverse', '\%6l.*')
  call matchadd('TestBlend', '\%7l.*')

  normal! gg
endfunction

function! ChecklistSearchSetup() abort
  call <SID>Log('search setup: hlsearch and visual-check buffer')
  enew
  call setline(1, [
        \ 'alpha test alpha',
        \ 'search highlight sample',
        \ 'visual selection sample',
        \ 'diagnostic underline substitute'
        \ ])
  set hlsearch
  execute 'normal! gg/test\<CR>'
endfunction

function! ChecklistDiffSetup() abort
  call <SID>Log('diff setup: create temp files and open vert diffsplit')
  let l:left = tempname() . '.left.txt'
  let l:right = tempname() . '.right.txt'
  call writefile([
        \ 'line 1',
        \ 'line 2 left',
        \ 'line 3',
        \ 'line 4 only-left'
        \ ], l:left)
  call writefile([
        \ 'line 1',
        \ 'line 2 right',
        \ 'line 3',
        \ 'line 4 only-right'
        \ ], l:right)

  execute 'edit ' . fnameescape(l:left)
  execute 'vert diffsplit ' . fnameescape(l:right)
endfunction

function! ChecklistInputMaps() abort
  nnoremap <silent> <C-j> :echo "ctrl-j"<CR>
  nnoremap <silent> <S-Tab> :echo "shift-tab"<CR>
  nnoremap <silent> <M-x> :echo "alt-x"<CR>
  nnoremap <silent> <F6> :echo "f6"<CR>
  set mouse=a
  call <SID>Log('input maps ready: <C-j>, <S-Tab>, <M-x>, <F6>, mouse=a')
  echo 'Input maps ready: <C-j>, <S-Tab>, <M-x>, <F6>, mouse=a'
endfunction

function! ChecklistCmdlineNotes() abort
  call <SID>Log('cmdline notes: :, /, ?, Enter, Esc')
  echo join([
        \ 'Cmdline checks:',
        \ '1. press : and confirm dedicated cmdline layer',
        \ '2. run :echo ''test'' and confirm message layer',
        \ '3. press / and ? for search cmdline',
        \ '4. confirm Esc/Enter hide cmdline'
        \ ], ' ')
endfunction

function! ChecklistHelp() abort
  call <SID>Log('help shown')
  echo join([
        \ 'Run:',
        \ ':call ChecklistFloatingBasic()',
        \ ':call ChecklistFloatingAlt()',
        \ ':call ChecklistHighlightSetup()',
        \ ':call ChecklistSearchSetup()',
        \ ':call ChecklistDiffSetup()',
        \ ':call ChecklistInputMaps()',
        \ ':call ChecklistCmdlineNotes()'
        \ ], ' ')
endfunction

function! ChecklistRunAll() abort
  call <SID>Log('run all: start')
  call ChecklistCmdlineNotes()
  call ChecklistFloatingBasic()
  call ChecklistFloatingAlt()
  call ChecklistHighlightSetup()
  call ChecklistSearchSetup()
  call ChecklistDiffSetup()
  call ChecklistInputMaps()
  call <SID>Log('run all: finished, inspect :messages or :ChecklistLog')
endfunction

function! ChecklistLog() abort
  if empty(g:checklist_log)
    echo 'checklist log is empty'
    return
  endif

  let l:buf = nvim_create_buf(v:false, v:true)
  call nvim_buf_set_lines(l:buf, 0, -1, v:false, ['Checklist log:'] + g:checklist_log)
  call nvim_open_win(l:buf, v:true, {
        \ 'relative': 'editor',
        \ 'row': 2,
        \ 'col': 4,
        \ 'width': 70,
        \ 'height': min([len(g:checklist_log) + 2, 16]),
        \ 'style': 'minimal',
        \ 'border': 'single'
        \ })
endfunction

function! ChecklistClearLog() abort
  let g:checklist_log = []
  echo 'checklist log cleared'
endfunction

command! ChecklistHelp call ChecklistHelp()
command! ChecklistFloatingBasic call ChecklistFloatingBasic()
command! ChecklistFloatingAlt call ChecklistFloatingAlt()
command! ChecklistHighlightSetup call ChecklistHighlightSetup()
command! ChecklistHighlightReset call ChecklistHighlightReset()
command! ChecklistSearchSetup call ChecklistSearchSetup()
command! ChecklistDiffSetup call ChecklistDiffSetup()
command! ChecklistInputMaps call ChecklistInputMaps()
command! ChecklistCmdlineNotes call ChecklistCmdlineNotes()
command! ChecklistRunAll call ChecklistRunAll()
command! ChecklistLog call ChecklistLog()
command! ChecklistClearLog call ChecklistClearLog()
