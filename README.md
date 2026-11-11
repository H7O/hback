# hback

hback copies the folders you pick to another folder or to an SFTP server, on a schedule. After the
first run it copies only what changed.

The backup is made of ordinary files in ordinary folders. You can browse it with any file manager or
SFTP client and copy a file back by hand. Nothing about it needs hback to be read.

It runs on Windows, Linux and macOS, as a service or from the command line.

## What a backup looks like

Say you back up `C:\code` to `sftp://me@nas/backups/laptop`. On the server you get three folders:

```
/backups/laptop/
  current/C/code/app/Program.cs     the files as they are now
  history/2026-11-11_103000/...     the previous copy of each file a run replaced or removed
  partial/                          files on their way in
```

A file keeps its full original path under `current/`, with the drive colon dropped. So two sources can
never collide, and you can always tell where a file came from. On Linux, `/home/me/code/a.c` becomes
`current/home/me/code/a.c`.

A file is written into `partial/` first and moved into `current/` only when it is whole. So everything
under `current/` is a complete file, even if a run was cut off half way.

When a file changes or is deleted, hback does not destroy the copy it has. It moves that copy into
`history/`, in a folder named after the run. History older than `keep_history_days` (30 by default) is
deleted. This is what saves you from a file you overwrote or deleted by mistake last week.

## Install

Every [release](https://github.com/H7O/hback/releases/latest) has a download for each system. Take
yours, unpack it, and you have a folder named `hback` with the program in it. There is nothing else to
install: the program carries the .NET it needs.

| Your system | Download |
| --- | --- |
| Windows on Intel or AMD, which is most PCs | [hback-win-x64.zip](https://github.com/H7O/hback/releases/latest/download/hback-win-x64.zip) |
| Windows on ARM | [hback-win-arm64.zip](https://github.com/H7O/hback/releases/latest/download/hback-win-arm64.zip) |
| Linux on Intel or AMD | [hback-linux-x64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-linux-x64.tar.gz) |
| Linux on 64-bit ARM, such as a Raspberry Pi with a 64-bit system | [hback-linux-arm64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-linux-arm64.tar.gz) |
| Linux on 32-bit ARM, such as a Raspberry Pi with a 32-bit system | [hback-linux-arm.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-linux-arm.tar.gz) |
| Alpine Linux on Intel or AMD | [hback-linux-musl-x64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-linux-musl-x64.tar.gz) |
| Alpine Linux on 64-bit ARM | [hback-linux-musl-arm64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-linux-musl-arm64.tar.gz) |
| macOS on Apple silicon (M1 and later) | [hback-macos-arm64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-macos-arm64.tar.gz) |
| macOS on Intel | [hback-macos-x64.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-macos-x64.tar.gz) |
| Anything else that .NET 10 runs on | [hback-portable.zip](https://github.com/H7O/hback/releases/latest/download/hback-portable.zip) or [.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-portable.tar.gz) |

Not sure which Linux download is yours? `uname -m` names the processor: `x86_64` is Intel or AMD, and
`aarch64` or `armv7l` is ARM. `getconf LONG_BIT` says whether the system is 32-bit or 64-bit.

Each release also has a `SHA256SUMS.txt` with the checksum of every download.

### Windows

In PowerShell, in the folder you downloaded to:

```powershell
Expand-Archive hback-win-x64.zip -DestinationPath C:\tools
C:\tools\hback\hback version
```

That puts hback in `C:\tools\hback`. Another folder is fine as long as you can write to it, which
rules out `C:\Program Files`: hback keeps its configuration, its database and its logs in its own
folder.

hback is not signed, so Windows may say it protected your PC from an unrecognized app. Choose
"More info" and then "Run anyway". To avoid the question, run `Unblock-File hback-win-x64.zip` before
unpacking.

It needs Windows 10 or Windows Server 2012, or later.

### Linux

```sh
curl -LO https://github.com/H7O/hback/releases/latest/download/hback-linux-x64.tar.gz
sudo mkdir /opt/hback
sudo chown $USER /opt/hback
tar -xzf hback-linux-x64.tar.gz -C /opt/hback --strip-components=1
/opt/hback/hback version
```

That puts hback in `/opt/hback` and makes the folder yours. It has to be yours, because hback keeps
its configuration, its database and its logs in it. To type `hback` from anywhere:

```sh
sudo ln -s /opt/hback/hback /usr/local/bin/hback
```

It needs glibc 2.27 or later, which Ubuntu 18.04, Debian 10 and everything newer have. The 32-bit ARM
download needs glibc 2.35: Debian 12, Raspberry Pi OS Bookworm, Ubuntu 22.04, or newer. It does not
run on the Raspberry Pi 1 or the original Pi Zero, whose processor .NET doesn't support.

On Alpine, install two libraries first: `apk add libgcc libstdc++`.

### macOS

```sh
curl -LO https://github.com/H7O/hback/releases/latest/download/hback-macos-arm64.tar.gz
tar -xzf hback-macos-arm64.tar.gz -C ~
~/hback/hback version
```

That puts hback in the `hback` folder of your home folder.

Download it with `curl` as shown, not with a browser. macOS marks every file a browser downloads, and
then refuses to run a program that doesn't come from a developer registered with Apple. If you did use
a browser, remove the mark: `xattr -dr com.apple.quarantine ~/hback`.

It needs macOS 15 or later.

### Any other system

`hback-portable` has no .NET inside it. Install the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0), unpack the download, and
wherever this page says `hback`, type `dotnet hback.dll` with the path of that file.

### Updating

Stop hback if it runs as a service, unpack the new download over the old folder, and start it again.
Unpacking is the same `tar` line as above, or the same `Expand-Archive` line with `-Force` added. Your
`hback.xml` and the `data` folder are not part of a download, so an update leaves them alone.

## Your first backup

From here on this page writes `hback` for the program. Type its path instead (`C:\tools\hback\hback`,
`/opt/hback/hback`, `~/hback/hback`) unless you made it reachable from anywhere.

1. In hback's folder, copy `hback.sample.xml` to `hback.xml` and edit it. Under `<sources>` list the
   folders to back up, and in `<destination>` say where the copies go. The sample is written for
   Windows. On Linux and macOS write paths the way your system does, such as `/home/me/code` or
   `~/Documents/notes`. [Configuration](#configuration) explains every setting.

2. See what it would do, without copying anything:

   ```
   hback run --dry-run
   ```

   For a first backup this also shows where the space goes, folder by folder, which is the quickest
   way to find what to exclude.

3. Run it for real, and look at how it went:

   ```
   hback run
   hback status
   ```

4. Have it run by itself from now on: see [Running it as a service](#running-it-as-a-service).

## Configuration

`hback.xml` holds one `<backup>` entry per backup. hback re-reads the file while it runs, and a backup
you edit runs straight away.

```xml
<hback>
  <backup>
    <name>projects</name>
    <sys>
      <interval>3600000</interval>
    </sys>
    <sources>
      C:\code
      ~\Documents\notes
    </sources>
    <exclude>
      node_modules/
      bin/
      obj/
    </exclude>
    <destination>sftp://me@backup.example.com:22/backups/laptop</destination>
    <private_key>~\.ssh\id_ed25519</private_key>
  </backup>
</hback>
```

| Tag | Meaning |
| --- | --- |
| `name` | The backup's name, used in commands and logs. Each backup needs its own. |
| `sys` | When it runs. See below. Leave it out for a backup that only runs with `hback run`. |
| `sources` | Folders and files to back up, one per line, as full paths. `~` is your home folder. |
| `exclude` | What to leave out, one pattern per line. See below. |
| `destination` | A folder (`D:\backups\laptop`), or `sftp://user@host:port/folder`. Use `sftp://user@host/~/folder` for a folder under the login folder. Each backup needs a destination folder of its own. |
| `private_key` | For SFTP: the private key file. OpenSSH and PEM formats both work. |
| `passphrase` | The key's passphrase, if it has one. |
| `password` | For SFTP: a password, when you don't use a key. |
| `host_key` | Optional. The server's host key fingerprint (`SHA256:...`). See "Host keys". |
| `keep_history_days` | How long replaced and deleted files stay in `history/`. Default 30. `0` keeps none: a changed file is overwritten and a deleted file is removed. |
| `parallel` | How many files are copied at once. Default 4. Each uses its own connection. |
| `retries`, `retry_wait_minutes` | When the destination can't be reached, the service tries again this many times (default 3), this far apart (default 5 minutes), before waiting for the next scheduled run. |

A local destination folder must exist already. hback doesn't create it, so that an unplugged USB
drive or an unmounted share is never mistaken for an empty destination.

A path with `&` in it must be written with `&amp;`, because this is XML.

### When it runs

The `<sys>` element belongs to [Com.H.Threading.Scheduler](https://github.com/H7O/Com.H.Threading.Scheduler),
and everything that scheduler offers works here. The two you are most likely to want:

```xml
<sys><interval>3600000</interval></sys>   <!-- every hour (the number is milliseconds) -->
<sys><time>01:00</time></sys>             <!-- once a day, at or after 01:00 -->
```

A daily backup whose time was missed, because the machine was off or asleep, runs as soon as hback is
running again that day. A run with nothing to copy only looks at file sizes and times and does not
contact the destination, so a short interval is cheap.

### What to exclude

Patterns are written like lines of a `.gitignore`:

| Pattern | Leaves out |
| --- | --- |
| `node_modules` | anything with that name, in any folder |
| `bin/` | folders with that name (a trailing slash means folders only) |
| `*.log` | by wildcard: `*` within a name, `?` one character, `[Bb]` one of a set |
| `/build/output` | a path from the top of each source folder |
| `docs/**/*.pdf` | `**` reaches across folders |
| `C:/code/old/` | one folder, by its full path |

There is no `!` to bring a file back.

hback does not read your `.gitignore` files, on purpose. They leave out exactly the files that exist
nowhere else and that a backup should keep: `.env` files, local settings, uncommitted work. Your `.git`
folders are backed up too, unless you exclude them, so unpushed commits and stashes are covered.

## Commands

```
hback                     Follow the schedule in hback.xml until stopped.
hback run [name ...]      Run the named backups now, or all of them.
    --dry-run             Only say what would be copied and removed.
hback status [name]       How the last run of each backup went. With a name, its last 10 runs.
hback verify [name ...]   Check that every file hback recorded is at the destination.
    --fix                 Forget the ones that are missing, so the next run copies them again.
hback restore <name> --to <folder> [--path <path>] [--at <history folder>]
```

`--config <path>` points at another `hback.xml` (or a folder of `.xml` files), and `--data <folder>`
at another place for hback's own database and logs. By default both are beside the program. The
`HBACK_CONFIG` and `HBACK_DATA` environment variables do the same.

## Restoring

The simplest restore is to copy what you need out of `current/` with any tool. hback can do it too:

```
hback restore projects --to D:\restored
hback restore projects --to D:\restored --path C:\code\app
hback restore projects --to D:\restored --path C:\code\app --at 2026-11-11_103000
```

Files land under the folder you name, in the same layout as the backup (`D:\restored\C\code\app\...`).
hback never writes over the original location by itself. `--at` takes the name of a folder under
`history/` and restores the copies that run set aside.

## Running it as a service

Started with no command, hback follows the schedule in `hback.xml` until it is stopped. Each system
has its own way of starting a program like that with the machine and keeping it running.

### Linux (systemd)

Save this as `/etc/systemd/system/hback.service`, with your user name in place of `me`:

```ini
[Unit]
Description=hback
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=me
ExecStart=/opt/hback/hback
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Then start it, now and at every boot:

```sh
sudo systemctl enable --now hback
```

`User` is whoever owns `/opt/hback`, and `~` in `hback.xml` is that user's home folder.

Under systemd, hback writes to the journal with proper levels: `journalctl -u hback -p warning` shows
only what went wrong. `sudo systemctl stop hback` stops it, and `sudo systemctl disable hback` keeps it
from starting at boot.

Alpine has no systemd. Start `hback` from the service manager you use there, or see "Without a
service" below.

### Windows

From an administrator prompt:

```
sc.exe create hback binPath= "C:\tools\hback\hback.exe" start= delayed-auto
sc.exe start hback
```

A Windows service runs as the system account unless told otherwise. That account's home folder is not
yours, so write full paths instead of `~` in `hback.xml`, and it can't see mapped network drives. To
run it as yourself: `sc.exe config hback obj= ".\yourname" password= "..."`.

`sc.exe stop hback` stops it, and `sc.exe delete hback` removes the service.

### macOS (launchd)

Save this as `~/Library/LaunchAgents/com.github.h7o.hback.plist`, with your user name in place of `me`
in both paths:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.github.h7o.hback</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Users/me/hback/hback</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <dict>
    <key>SuccessfulExit</key>
    <false/>
  </dict>
  <key>StandardErrorPath</key>
  <string>/Users/me/Library/Logs/hback.log</string>
</dict>
</plist>
```

Then start it, now and every time you log in:

```sh
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.github.h7o.hback.plist
```

hback runs as you, while you are logged in. If it could not start, say because `hback.xml` has a
mistake in it, the reason is in `~/Library/Logs/hback.log`.
`launchctl bootout gui/$(id -u)/com.github.h7o.hback` stops it.

macOS decides which programs may read your Documents, Desktop and Downloads folders and your external
drives. If a backup reports files there that it could not read, add `hback` under System Settings >
Privacy & Security > Full Disk Access. After an update macOS may want that done again.

### Without a service

`hback run` from cron or Task Scheduler works as well. The `<sys>` element is then not used.

## What happens when

**A run is cut off** (power loss, a dropped connection, hback stopped). Nothing is lost and nothing is
half-done in `current/`. The next run copies what is still missing. A file of 8 MB or more that was
partly sent continues from where it stopped, as long as the source hasn't changed since.

**The server can't be reached.** The run fails with the reason, which `hback status` shows. The
service tries again a few times, then waits for the next scheduled run.

**A file can't be read**, because another program holds it locked. It is reported, the rest is copied,
and the run is marked as finished with problems.

**A file changes while it is being copied.** hback notices and copies it again on the next run.

**A source folder is missing**, say an unplugged drive. Its files in the backup are left alone. hback
only treats a file as deleted when it could look and the file wasn't there.

**A file's time changed but its content didn't** (switching git branches back and forth does this).
hback compares a hash of the file with the one it stored and sends nothing.

**hback's own database is lost**, or the machine is set up again. Point hback at the same destination.
It sees that files are already there and records the ones that match the source by size and time,
instead of sending everything again.

**Two runs at once.** The service and a manual `hback run` never write to the same destination
together. The second one is skipped.

**`hback.xml` is not valid XML.** The service refuses to start, says what is wrong and on which line,
and exits with code 2. If the file breaks while the service is running, say it was saved half way
through an edit, the service logs it and carries on with the backups it already knew until the file is
fixed. A mistake inside one backup, such as a missing `<sources>`, stops only that backup.

### Host keys

The first time hback connects to an SFTP server it remembers the server's host key, and logs it. From
then on it refuses to connect if the server shows a different one, which is what protects your files
from being sent to an impostor. If the server really was reinstalled, the error message tells you the
line to add to `hback.xml`. To check the key before the first connection instead of trusting it, put
`<host_key>SHA256:...</host_key>` in the backup's entry.

## What hback does not do

- It does not encrypt or compress what it stores. The files at the destination are readable by whoever
  can read the destination. SFTP protects them on the way there.
- It does not keep file permissions, owners or Windows ACLs. It keeps content, names and modified times.
- It does not follow symbolic links or junctions, and does not back them up.
- It is one-way. Nothing is ever copied from the destination back to your sources unless you run `restore`.
- Don't point two machines at the same destination folder. Give each its own.

## Building and testing

It needs the .NET 10 SDK.

```
dotnet build
dotnet test
```

The tests run every scenario twice: against a local folder, and against a real SFTP server. For the
second they start [portable-sftp](https://github.com/H7O/portable-sftp) on a free local port. Tell them
where it is, or have it on your PATH:

```
set HBACK_TEST_SFTP_SERVER=C:\tools\portable-sftp.exe
```

Without it, the SFTP tests report themselves as skipped. Set `HBACK_TEST_SFTP_REQUIRED=1` as well to
have them fail instead, which is what a build machine wants.

To build a download yourself, as one program file with .NET inside it:

```
dotnet publish src/hback -c Release -r linux-x64 --self-contained -o publish/hback
```

In place of `linux-x64` goes .NET's name for your system: `win-x64`, `win-arm64`, `linux-arm64`,
`linux-arm`, `linux-musl-x64`, `linux-musl-arm64`, `osx-arm64` or `osx-x64`.

### Releasing

A tag that starts with `v` publishes a release:

```
git tag v0.1.2
git push origin v0.1.2
```

The [release workflow](.github/workflows/release.yml) then runs on Linux, Windows and macOS. On each
it runs the tests, builds that system's downloads, and makes a real backup with them, by hand and as a
service. It publishes the release only if all of that passed. The version comes from the tag. A tag
with a hyphen in it, such as `v0.2.0-beta.1`, is published as a prerelease.

"Run workflow" on the repository's Actions page does the same without publishing anything.

## Built on

- [Com.H.Threading.Scheduler.DI](https://github.com/H7O/Com.H.Threading.Scheduler.DI) decides when a backup is due and re-reads the configuration when it changes.
- [Com.H.Net.Ssh](https://github.com/H7O/Com.H.Net.Ssh) (over SSH.NET) is the SFTP connection.
- [Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common) runs the SQL against hback's SQLite state database.
- [Com.H.Extensions.Logging.File](https://www.nuget.org/packages/Com.H.Extensions.Logging.File) writes the daily log files.
