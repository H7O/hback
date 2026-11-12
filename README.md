# hback

hback copies the folders you pick to another folder or to an SFTP server, on a schedule. After the
first run it copies only what changed.

The backup is made of ordinary files in ordinary folders. You can browse it with any file manager or
SFTP client and copy a file back by hand. Nothing about it needs hback to be read.

It runs on Windows, Linux and macOS, as a service or from the command line.

One backup can go to several places. That also makes hback a way to keep a folder on other machines
the same as the folder on this one: see [Sending a folder to other machines](#sending-a-folder-to-other-machines).

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

This is the layout unless you ask for another. [Where the files go](#where-the-files-go) shows how to
change it, down to putting the files straight into the destination folder.

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
| Anything else that .NET 11 runs on | [hback-portable.zip](https://github.com/H7O/hback/releases/latest/download/hback-portable.zip) or [.tar.gz](https://github.com/H7O/hback/releases/latest/download/hback-portable.tar.gz) |

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

It needs Windows 10 or Windows Server 2016, or later.

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
[.NET 11 runtime](https://dotnet.microsoft.com/download/dotnet/11.0), unpack the download, and
wherever this page says `hback`, type `dotnet hback.dll` with the path of that file.

### Updating

Stop hback if it runs as a service, unpack the new download over the old folder, and start it again.
Unpacking is the same `tar` line as above, or the same `Expand-Archive` line with `-Force` added. Your
`hback.xml` and the `data` folder are not part of a download, so an update leaves them alone.

## Your first backup

From here on this page writes `hback` for the program. Type its path instead (`C:\tools\hback\hback`,
`/opt/hback/hback`, `~/hback/hback`) unless you made it reachable from anywhere.

1. In hback's folder, copy `hback.sample.xml` to `hback.xml` and edit it. Under `<sources>` list the
   folders to back up, in `<destination>` say where the copies go, and in `<sys>` say when: every
   hour in the sample, and [When it runs](#when-it-runs) has the other ways. The sample is written
   for Windows. On Linux and macOS write paths the way your system does, such as `/home/me/code` or
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
    <sources><![CDATA[
      C:\code
      ~\Documents\notes
    ]]></sources>
    <exclude><![CDATA[
      node_modules/
      bin/
      obj/
    ]]></exclude>
    <destination><![CDATA[sftp://me@backup.example.com:22/backups/laptop]]></destination>
    <private_key><![CDATA[~\.ssh\id_ed25519]]></private_key>
  </backup>
</hback>
```

The paths, patterns, addresses and passwords on this page are written between `<![CDATA[` and `]]>`.
hback reads what is between those two marks exactly as it is written. So a `&`, a `<` or a `>` in a
path or a password needs nothing done to it, and a backslash is one backslash. That is the way to
write values of this kind, because they are the ones likely to hold such a character.

hback does not ask for CDATA anywhere. The names, numbers and times on this page are written without
it, and any value can be. A value without CDATA follows the rules of XML: `&` has to be written
`&amp;`, and `<` has to be written `&lt;`. The one thing CDATA can't hold is `]]>` itself.

An `sftp://` address is a URL whichever way it is written. `%20` in it is a space, though a space can
simply be typed, and a `%` in a folder name there is written `%25`.

| Tag | Meaning |
| --- | --- |
| `name` | The backup's name, used in commands and logs. Each backup needs its own. |
| `sys` | When it runs: every so often, at a time of day, on certain days, from a certain moment. See "When it runs". Leave it out for a backup that only runs with `hback run`. |
| `sources` | Folders and files to back up, one per line, as full paths. `~` is your home folder. |
| `exclude` | What to leave out, one pattern per line. See below. |
| `destination` | A folder (`D:\backups\laptop`), or `sftp://user@host:port/folder`. Use `sftp://user@host/~/folder` for a folder under the login folder. Each backup needs a destination folder of its own. |
| `destinations` | More than one destination, one per line. See "Several destinations". |
| `private_key` | For SFTP: the private key file. OpenSSH and PEM formats both work. |
| `passphrase` | The key's passphrase, if it has one. |
| `password` | For SFTP: a password, when you don't use a key. |
| `host_key` | Optional. The server's host key fingerprint (`SHA256:...`). See "Host keys". |
| `keep_history_days` | How long replaced and deleted files stay in `history/`. Default 30. `0` keeps none: a changed file is overwritten and a deleted file is removed. |
| `parallel` | How many files are copied at once. Default 4. Each uses its own connection. |
| `source_path` | How much of each source's own path is repeated at the destination: `full` (the default), `name` or `none`. See "Where the files go". |
| `current_folder`, `history_folder`, `partial_folder` | Other names or places for hback's three folders inside the destination. See "Where the files go". |
| `retries`, `retry_wait_minutes` | When a destination can't be reached, the service tries again this many times (default 3), this far apart (default 5 minutes), before waiting for the next scheduled run. |

A local destination folder must exist already. hback doesn't create it, so that an unplugged USB
drive or an unmounted share is never mistaken for an empty destination.

### Several destinations

A backup can go to more than one place. List them one per line in `<destinations>`, the way
`<sources>` lists folders:

```xml
<private_key><![CDATA[~\.ssh\id_ed25519]]></private_key>
<destinations><![CDATA[
  E:\backups\laptop
  sftp://me@backup.example.com/backups/laptop
  sftp://me@second.example.com/backups/laptop
]]></destinations>
```

Every destination gets all the files. hback serves them one after the other, in the order they are
listed, and looks through the sources only once for all of them. A destination that can't be reached
does not hold up the others, and when the service tries again it tries only that one. `hback status`
reports on each destination by itself.

The backup's other settings hold for every destination in the list. Here the one private key is used
for both servers. A destination that needs something of its own is written as a `<destination>`
element, with its address in `<path>` and its settings beside it. The others are then written as
`<destination>` elements too, because a list holds either lines or elements, not both:

```xml
<private_key><![CDATA[~\.ssh\id_ed25519]]></private_key>
<destinations>
  <destination><![CDATA[sftp://me@backup.example.com/backups/laptop]]></destination>
  <destination>
    <name>usb</name>
    <path><![CDATA[E:\backups\laptop]]></path>
    <keep_history_days>365</keep_history_days>
  </destination>
</destinations>
```

Such a destination can set anything in the table above from `private_key` to `partial_folder`. What
it doesn't set, it takes from the backup. Its `<name>` is optional. The log and `hback status` call
the destination by it, and `--destination` takes it on the command line.

Repeating `<destination>` in the backup itself, once for each place, does the same as the list.

### Where the files go

Two things about the layout at a destination can be changed. A backup needs neither.

The first is how much of each source's own path is repeated there, which `source_path` sets. Take the
file `C:\code\app\a.cs`, backed up from the source `C:\code`:

| `source_path` | The file is stored as | What it means |
| --- | --- | --- |
| `full` | `current/C/code/app/a.cs` | The whole path. This is the default. Two sources can never collide, and the backup itself shows where every file came from. |
| `name` | `current/code/app/a.cs` | Each source becomes a folder with the source's own name. Two sources of the same name can't be in one backup. |
| `none` | `current/app/a.cs` | Only what is inside the source. |

With `name` and `none`, files of two sources can want the same place. hback checks that before it
copies anything. If two files would be stored as one, the run fails and names the two.

The second is where hback's three folders are. `current_folder`, `history_folder` and
`partial_folder` each take a folder inside the destination, such as `files` or `kept/history`. For the
current folder, `.` means the destination folder itself:

```xml
<sources><![CDATA[C:\deploy\site]]></sources>
<destination><![CDATA[\\web1\apps\site]]></destination>
<source_path>none</source_path>
<current_folder><![CDATA[.]]></current_folder>
```

Now `C:\deploy\site\index.html` arrives as `\\web1\apps\site\index.html`, with nothing of hback's
around it. hback's other two folders then move out of the way, to `.hback/history` and
`.hback/partial` in the same folder, unless you name them yourself.

hback only ever touches the files it put somewhere itself. Other files in the folder are left alone.
If a file is already where hback is about to put one, that file is treated as an older copy: it goes to
history first, or is replaced when no history is kept.

Both settings can be changed on a backup that has already run. A new `source_path` rearranges the
destination on the next run: every file is copied to its new place, and the copies in the old places
go to history. A new `current_folder` starts afresh in that folder, and the old folder stays as it is
until you delete it.

Two backups can share one destination folder when each has three folders of its own there, none of
them inside a folder of the other. Otherwise each backup needs a destination folder of its own.

### When it runs

A backup's `<sys>` element says when it runs. The element belongs to
[Com.H.Threading.Scheduler](https://github.com/H7O/Com.H.Threading.Scheduler), which is what tells
hback that a backup is due. A backup has one `<sys>`. Without one it runs only when you type
`hback run`.

Three rules explain every schedule:

- **All the tags have to agree.** A backup runs when every tag in its `<sys>` allows it. Each tag you
  add narrows the schedule, and none widens it.
- **Without `<interval>`, a backup runs once a day**, on each day the other tags allow. It runs at its
  `<time>`, or as soon after that as it can. So a backup whose time was missed, because the machine
  was off or asleep, runs when hback is next running that day. And a backup you have just added or
  changed runs at once if its time has already passed today.
- **With `<interval>`, a backup runs again** each time that many milliseconds have gone by since its
  last run. `<time>` and `<until_time>` then mark out the part of the day in which it does.

Every time is read by the clock of the machine hback runs on.

| Tag | What it does | Written as |
| --- | --- | --- |
| `interval` | Runs the backup again every so many milliseconds. | `1800000` |
| `time` | The time of day a daily backup runs at. With `interval`, the time of day it starts at. | `05:00`, by the 24-hour clock |
| `until_time` | With `interval`, the time of day it stops at. It has to be later than `time`. | `18:00` |
| `dow` | Only on these days of the week. | `Monday,Thursday`, with the names in full |
| `dom` | Only on these days of the month. | `1,15`, or `25..28` for a run of days |
| `bom` | `true` means only on the first day of the month, the same as `<dom>1</dom>`. | `true` |
| `eom` | `true` means only on the last day of the month. | `true` |
| `doy` | Only on these days of the year, counted from 1. | `1,182` |
| `dates` | Only on these dates. | `2026-12-24`, or `2026-12-24\|2026-12-31` for two |
| `not_before` | Not before this moment. | `2026-11-12 16:30` |
| `not_after` | Not after this moment. | `2026-12-31 23:59` |
| `enabled` | `false` switches the schedule off. `hback run` still runs the backup. | `false` |
| `ignore_log_on_restart` | `true` makes hback forget, each time it starts, when the backup last ran. So the backup runs again then, if the other tags allow it. | `true` |

An interval is a number of milliseconds: `60000` is a minute, `300000` is five minutes, `1800000` is
half an hour, `3600000` is an hour, `21600000` is six hours and `43200000` is twelve.

Here is how the schedules people ask for are written:

```xml
<!-- Every hour. -->
<sys><interval>3600000</interval></sys>

<!-- Every day at 05:00. -->
<sys><time>05:00</time></sys>

<!-- Every day at 05:00, and the first time today at 16:30. Today is 12 November 2026 here. -->
<sys>
  <time>05:00</time>
  <not_before>2026-11-12 16:30</not_before>
</sys>

<!-- Every 30 minutes from 08:00 to 18:00, Monday to Friday. -->
<sys>
  <interval>1800000</interval>
  <time>08:00</time>
  <until_time>18:00</until_time>
  <dow>Monday,Tuesday,Wednesday,Thursday,Friday</dow>
</sys>

<!-- Once a week, on Sunday at 03:00. -->
<sys>
  <time>03:00</time>
  <dow>Sunday</dow>
</sys>

<!-- On the 1st and the 15th of every month at 02:00. -->
<sys>
  <time>02:00</time>
  <dom>1,15</dom>
</sys>

<!-- On the last day of every month at 23:00. -->
<sys>
  <time>23:00</time>
  <eom>true</eom>
</sys>

<!-- Once only, on 24 December 2026 at 16:30. -->
<sys>
  <time>16:30</time>
  <dates>2026-12-24</dates>
</sys>

<!-- Every night at 01:00 until the end of 2026, and not after that. -->
<sys>
  <time>01:00</time>
  <not_after>2026-12-31 23:59</not_after>
</sys>

<!-- Switched off for now. "hback run" still runs it. -->
<sys>
  <time>05:00</time>
  <enabled>false</enabled>
</sys>
```

The third one follows from the second rule. On the day it names, `not_before` holds the backup back
until 16:30. Its 05:00 has passed by then, so it runs at once. From the next day on it runs at 05:00.
Without `not_before`, a backup added in the afternoon would run the moment it is added.

One `<sys>` holds one time of day. A backup that should run several times a day takes an `interval`.

To see how hback reads a schedule, run `hback status`. It says each backup's schedule in words, such
as `once a day at 05:00, not before 2026-11-12 16:30`. A tag that `<sys>` doesn't have, or a value
that can't be read, such as `<time>5am</time>`, is refused. So are `dom`, `bom` and `eom` when no day
fits them all, such as `bom` beside `eom`, because that backup would never run. `hback status` and
the log then say what is wrong and what to write instead, and that backup does not run until it is
put right. `hback status` also ends with exit code 2 when it finds a problem like that in the
configuration, and with 0 when there is none.

The scheduler has tags of its own for trying again after an error. They have no use here, because
hback tries again by `retries` and `retry_wait_minutes`.

A run with nothing to copy only looks at file sizes and times and does not contact the destination,
so a short interval is cheap.

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

## Sending a folder to other machines

hback was made for backups, but the same run does the work of a file distributor: it keeps a folder on
other machines the same as a folder on this one. Say a new version of a site is put into
`C:\deploy\site` on one machine, and three servers should have it:

```xml
<backup>
  <name>site</name>
  <sys><interval>300000</interval></sys>
  <sources><![CDATA[C:\deploy\site]]></sources>
  <source_path>none</source_path>
  <current_folder><![CDATA[.]]></current_folder>
  <keep_history_days>7</keep_history_days>
  <private_key><![CDATA[~\.ssh\id_ed25519]]></private_key>
  <destinations><![CDATA[
    \\web1\apps\site
    sftp://deploy@web2.example.com/var/www/site
    sftp://deploy@web3.example.com/var/www/site
  ]]></destinations>
</backup>
```

Every five minutes, which is the interval here, hback looks at `C:\deploy\site`. What was added or
changed there is copied to each destination, and what was deleted there is removed from each. When
nothing changed, it doesn't even connect.

A file never shows up half-written. It is sent to `.hback/partial` first and moved into place when all
of it has arrived.

Nothing a run replaces or removes is lost at once. It is kept in `.hback/history` at that destination
for `keep_history_days`, so every server holds its own way back to last week's files. `0` keeps
nothing.

A server that is down misses nothing for good. Its run fails, the others go ahead, and it is brought up
to date when it can be reached again.

Three things to know before you rely on it:

- **It is not instant.** A change waits for the next run. An interval of a minute is fine, because a
  run with nothing to do costs next to nothing.
- **The files of one change arrive one after another**, not all at the same moment. For a short while
  a destination holds some new files beside some old ones.
- **It goes one way.** hback compares the source with its own record of what it sent. A file that
  someone changes on a server is not copied back, and is not put right either until it changes at the
  source. `hback verify` finds files that went missing or changed size, and with `--fix` the next run
  sends them again.

To keep `.hback` out of a folder that is served to the public, name the folder above it as the
destination and the folder itself as the current folder:

```xml
<destination><![CDATA[sftp://deploy@web2.example.com/var/www]]></destination>
<current_folder><![CDATA[site]]></current_folder>
```

The files then go to `/var/www/site`, and hback's two folders are beside it: `/var/www/history` and
`/var/www/partial`.

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

For a backup with several destinations, `run`, `verify` and `restore` take `--destination` with the
destination's `<name>`, or its number in the list. `hback run site --destination 2` sends the files to
the second one only. Without it, `run` and `verify` go through them all, and `restore` reads the first.

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

`--path` takes the original path whatever the layout is. With `source_path` set to `name` or `none`,
the restored files have that shorter layout too: `D:\restored\app\...`.

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
service tries again a few times, then waits for the next scheduled run. When a backup has several
destinations, the others get their files all the same, and only the one that failed is tried again.

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

It needs the .NET 11 SDK.

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
git tag v0.4.1
git push origin v0.4.1
```

The [release workflow](.github/workflows/release.yml) then runs on Linux, Windows and macOS. On each
it runs the tests, builds that system's downloads, and makes a real backup with them, by hand and as a
service. It publishes the release only if all of that passed. The version comes from the tag. A tag
with a hyphen in it, such as `v0.5.0-beta.1`, is published as a prerelease.

"Run workflow" on the repository's Actions page does the same without publishing anything.

## Built on

- [Com.H.Threading.Scheduler.DI](https://github.com/H7O/Com.H.Threading.Scheduler.DI) decides when a backup is due and re-reads the configuration when it changes.
- [Com.H.Net.Ssh](https://github.com/H7O/Com.H.Net.Ssh) (over SSH.NET) is the SFTP connection.
- [Com.H.Data.Common](https://github.com/H7O/Com.H.Data.Common) runs the SQL against hback's SQLite state database.
- [Com.H.Extensions.Logging.File](https://www.nuget.org/packages/Com.H.Extensions.Logging.File) writes the daily log files.

## License

hback is free to use, change and share under the [MIT license](LICENSE).
