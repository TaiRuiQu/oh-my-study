# Library location, backups, recovery, and deletion

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Library-zh-CN.md)

![Data boundaries: the local library retains originals and records; selected providers receive images and context; optional code follows a separate execution policy.](images/data-boundaries.png)

## Know what you are protecting

The default is %LOCALAPPDATA%/OhMyStudy/Library, but Settings → Library shows the actual selected path. Open folder is the easiest way to identify it. The library contains a SQLite database and managed assets such as originals, rendered pages, and thumbnails.

Your exported PDF is only a derivative. It does not retain every editable field, reference relationship, source asset, or task history.

## Copy and switch location

1. Save question edits and settings.
2. End active tasks and model requests.
3. Choose Change location and a parent folder.
4. Review the proposed OhMyStudy资料库 destination.
5. Confirm Copy and switch and wait for completion.
6. Check the displayed path and open a known exam.

The destination must not already exist. The app creates a coherent backup-style copy and switches to it; the original is retained. If switching fails after copying, inspect the message rather than assuming the copy was discarded or the active library changed.

## Make a manual backup

Save, exit, and copy the complete folder to a separate location. Quitting matters because SQLite can keep committed transactions in its WAL file. Copying only study.sqlite3 while running can omit data; copying only database files also omits originals.

Use a destination with adequate space and avoid overwriting your only good backup. Label backups so that you can identify their date and app version. A second folder on the same disk helps against accidental edits but not loss of that disk.

## Open or recover material

Select existing library opens a complete current-version library; it is not a merge operation. Preserve your backup and select a working copy. If rejected, keep the files intact and check version compatibility instead of creating a new database over them.

Credentials are account-specific and are not exported with the library. You may need to re-enter keys after changing machine/account. Older libraries need compatible migration tooling; internal migration services are not all exposed as current menu commands.

## Delete with the right scope

Delete exam removes its questions, references, task records, and managed original after confirmation. No application undo restores it. This does not mean deleting a provider/model removes that data: configuration deletion retains saved study content.

Before deletion, export useful review PDFs if desired and preserve the full library if you may need editable originals later. For an unavailable external drive, reconnect or select an intact library copy; do not interpret a missing drive as permission to replace your materials.

Implementation and reference: [MainWindow.LibrarySettings.cs](../../src/OhMyStudy.App/MainWindow.LibrarySettings.cs) · [LibraryBackup.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryBackup.cs) · [LibraryPapers.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryPapers.cs)


## Current library upgrade

The schema is v5. Supported older writable libraries migrate recursive questions to shared stems/ordered items and add generated-image ownership. A pre-upgrade `study.sqlite3.pre-v5-…` database snapshot is kept. This is not a complete asset backup: save, exit, and copy the whole library before upgrading. Older apps cannot open the converted library; keep a complete earlier backup if rollback may be needed. See [storage and migration](Tech-Storage-Recovery.md).

---

[Previous chapter](Features-Export.md) · [Section contents](Feature-Guide.md) · [Next chapter](Features-Troubleshooting.md)
