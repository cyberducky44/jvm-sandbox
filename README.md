# jvm-sandbox

Runs Minecraft (or any JVM) inside a Windows **AppContainer**, so a malicious mod can't
read your browser cookies, Discord tokens, SSH keys or documents, and can't install itself
to run at startup.

The sandbox is enforced by the Windows kernel on the process, not by anything inside the JVM.
The JVM runs unmodified: no agent, no bytecode rewriting, no SecurityManager, so there is no
runtime performance cost, and native code (JNI) is confined just the same as Java code.

## Build

    build.cmd

Uses the C# compiler that ships with Windows; no SDK needed. Output: `bin\jvm-sandbox.exe`.

## Use

    jvm-sandbox [options] [--] <java.exe> [java args...]

**Prism Launcher / MultiMC:** Settings → Custom commands → *Wrapper command*:

    C:\path\to\jvm-sandbox.exe

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

## What the game can touch

Granted automatically, worked out from the command line and environment:

- read: the Java install, everything on the classpath / module path, `-jar`, `-javaagent`,
  `java.library.path`, `--assetsDir`, and the launcher's `libraries` and `assets` folders
- read + write: `--gameDir`, `%INST_DIR%` (the Prism/MultiMC instance), and a private temp folder
- outbound internet (unless `--no-net`)

Everything else in your user profile and on other drives is denied, as is writing to the
registry or to any startup location.

The first run adds permissions to the folders above. Parent folders of those paths get a
"list names only" permission, because Java resolves paths by looking each component up in its
parent. For `C:\` and `C:\Users` that takes one admin prompt, once.

## Limits

- A mod runs inside the game, so it can still use the game's own session token and anything
  in the instance folder. Don't point the game directory at a folder holding other secrets.
- The sandbox can see file and folder *names* (not contents) in the parent folders of granted
  paths, e.g. the top level of your user folder.
- Connections to `localhost` are blocked by Windows for AppContainers.
- Windows only. The official Minecraft launcher has no wrapper-command setting, so it can't
  be used directly.

## Test

`test\Probe.java` checks from inside the sandbox what is allowed and what is denied:

    cd <some empty dir containing Probe.java>
    jvm-sandbox --rw . -- java Probe.java . C:\path\to\a\file\outside
