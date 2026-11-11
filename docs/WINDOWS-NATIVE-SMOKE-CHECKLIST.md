# Windows native smoke checklist

Run these checks only in a disposable Windows test account or VM. They are manual integration checks; automated tests use fake native APIs and do not register a real global hot key, create a notification-area icon, or access the system clipboard.

- [ ] Start the app while another test process owns `Ctrl+Shift+Space`; the app still starts and reports the shortcut conflict in a readable way.
- [ ] Release the conflicting shortcut, start the app, and verify `Ctrl+Shift+Space` activates the main window once per press.
- [ ] Enable the tray setting, verify the icon appears and its menu opens the window, pauses/resumes capture, and exits cleanly.
- [ ] Disable and re-enable the tray setting repeatedly; verify no duplicate or stale icons remain.
- [ ] Restart Windows Explorer and verify the tray icon is restored without restarting the app.
- [ ] Change the Windows app theme between light and dark; verify the app updates its theme once per actual change.
- [ ] Start and stop clipboard monitoring repeatedly; verify capture stops after shutdown and no native window or hot key remains after app exit.