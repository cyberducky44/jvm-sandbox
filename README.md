# jvm-sandbox

Runs Minecraft (or any JVM) inside a Windows **AppContainer**, so a malicious mod can't
read your browser cookies, Discord tokens, SSH keys or documents, and can't install itself
to run at startup.

The sandbox is enforced by the Windows kernel on the process, not by anything inside the JVM.
The JVM runs unmodified: no agent, no bytecode rewriting, no SecurityManager, so there is no
runtime performance cost, and native code (JNI) is confined just the same as Java code.

## Build

    build.cmd

Uses the C# compiler that ships with Windows; no SDK needed. Output: `bin\jvm-sandbox.exe`
(console, for terminals) and `bin\jvm-sandboxw.exe` (no console window, for launchers).

## Use

    jvm-sandbox [options] [--] <java.exe> [java args...]

**Lunar Client** (tested with 26.3 + Fabric): turn on Advanced Mode (the shield next to the
settings search), then set *Wrapper Command*. Use forward slashes, because Lunar's parser
eats backslashes:

    C:/path/to/jvm-sandboxw.exe

**Prism Launcher / MultiMC** (untested): Settings → Custom commands → *Wrapper command*:

    C:\path\to\jvm-sandboxw.exe

**Anything else:** put `jvm-sandbox.exe` in front of the Java command line.

| Option | Effect |
| --- | --- |
| `--rw <path>` | extra path the game may read and write |
| `--ro <path>` | extra path the game may read |
| `--name <id>` | separate sandbox identity, so instances can't read each other |
| `--no-net` | no network at all |
| `--lan` | also allow LAN connections and listening (LAN worlds, hosting) |
| `--no-elevate` | never show the one-time admin prompt |
| `--verbose` | print every permission that gets granted |
| `--log <file>` | also append the wrapper's messages to a file |

## What the game can touch

Granted automatically, worked out from the command line and environment:

- read: the Java install, everything on the classpath / module path, `-jar`, `-javaagent`,
  `java.library.path`, `--assetsDir`, and the launcher's `libraries` and `assets` folders
- read + write: `--gameDir`, `%INST_DIR%` (the Prism/MultiMC instance), `~/.lunarclient` when
  launched by Lunar, and a private temp folder
- outbound internet (unless `--no-net`)

Everything else in your user profile and on other drives is denied, as is writing to the
registry or to any startup location.

Two things are blocked even inside the writable folders:

- the official launcher's saved logins (`launcher_accounts*`, `launcher_msa_credentials*`) in
  the game directory can't be read
- the Java install can't be written to, so a mod can't plant code there

The first run adds permissions to the folders above. Parent folders of those paths get a
"list names only" permission, because Java resolves paths by looking each component up in its
parent. For `C:\` and `C:\Users` that takes one admin prompt, once.

## Limits

- A mod runs inside the game, so it can still use the game's own session token and anything
  in the instance folder. Don't point the game directory at a folder holding other secrets.
- A mod can still modify other files in the game folder (other mods, jars). Keep launching
  through the sandbox; a tampered file would run unsandboxed if you stop.
- The sandbox can see file and folder *names* (not contents) in the parent folders of granted
  paths, e.g. the top level of your user folder.
- Connections to `localhost` are blocked by Windows for AppContainers. With Lunar this cuts
  the link between the game and the launcher window; the game itself, login and Lunar's
  online services work.
- Windows only. The official Minecraft launcher has no wrapper-command setting, so it can't
  be used directly.

## Test

`test\Probe.java` checks from inside the sandbox what is allowed and what is denied:

    cd <some empty dir containing Probe.java>
    jvm-sandbox --rw . -- java Probe.java . C:\path\to\a\file\outside
