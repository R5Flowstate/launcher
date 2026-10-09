# r5f-host -- running an R5Flowstate dedicated server

`r5f-host` installs and updates a dedicated server, runs one or more server
instances, and manages Thunderstore mods. Updates download only the files that
changed.

## Install

```
r5f-host install --path D:\R5FServer
```

`--ring playtest` installs the playtest build instead of live. Playtest servers
are only listed to players who joined playtests in the launcher.

The install creates one instance called `main` on port 37015.

### Starting from the server zip

The zip already holds the current server, so there is nothing to download.
Extract it, then from that folder run:

```
r5f-host install --path .
```

or open `R5FlowstateServer.exe`. From then on `r5f-host update` (or the
Update button) fetches only the files that changed; you never need a new zip.

## Instances

```
r5f-host instance add duels --playlist fs_1v1 --map mp_rr_arena_composite --hostname "My 1v1"
r5f-host instance set main --visibility hidden --password -
r5f-host instance list
```

`--password -`, `--rcon-password -` and `--stats-key -` read the value from
stdin so it never lands in your shell history. Secrets are written only to
`platform/cfg/user/instance_<name>.cfg`, which updates never touch. A new
instance gets a random RCON password.

`--override VAR=VALUE` sets a playlist var for that instance (repeatable;
`VAR=` removes it). `--extra "..."` appends launch arguments.

## Running

```
r5f-host run                 # every instance, in the foreground
r5f-host run duels           # just one
r5f-host stop                # from another terminal
r5f-host logs main --lines 200
r5f-host status
```

`run` restarts an instance that crashes, waiting longer after each crash, and
stops trying after five crashes in ten minutes. Logs live in
`.r5f/host/logs/<name>.log`.

While `run` is up it checks for updates every 30 minutes. When one is out it
warns players in chat once a minute for 5 minutes, stops every instance,
updates, and starts them again. `--no-auto-update` turns that off. Both
intervals are in `.r5f/host/host.json` (`update_check_minutes`,
`update_grace_minutes`).

To keep a server up after logoff, run `r5f-host run` from Task Scheduler
(Windows) or a systemd unit (Linux):

```
[Unit]
Description=R5Flowstate server
After=network-online.target

[Service]
User=r5f
ExecStart=/opt/r5f-host/r5f-host run --path /srv/r5f --quiet
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

## Updating by hand

```
r5f-host check        # exit code 10 when an update is available
r5f-host update
r5f-host update --repair
```

Updates refuse to run while a server from that folder is running.

## Mods

```
r5f-host mods add Owner-ModName              # newest version, plus dependencies
r5f-host mods add Owner-ModName-1.2.0        # exact version
r5f-host mods add Owner-ModName --optional   # players may join without it
r5f-host mods import <profile code>          # a launcher/Thunderstore profile
r5f-host mods list
r5f-host mods update                         # move every mod to its newest version
r5f-host mods remove Owner-ModName
```

Mods are required by default: players without them get the install prompt in
the launcher when they join. Exact versions are pinned in
`.r5f/host/mods.lock.json`; nothing but `mods add` and `mods update` changes a
pin, so a server update never changes your mods. `mods sync` reinstalls exactly
what the lock file says (handy on a fresh box: copy `host.json` and
`mods.lock.json`, then `install` and `mods sync`).

## Linux

Experimental. `r5f-host` runs natively on Linux and starts the server through
Wine; the server itself has not been verified under Wine yet. Set the wine
binary in `host.json` (`"wine": "/usr/bin/wine64"`) if it is not on PATH.
