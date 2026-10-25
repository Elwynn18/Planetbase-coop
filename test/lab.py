#!/usr/bin/env python3
"""Test lab for Planetbase Co-op: isolated game copies under Proton, driven by in-game bots.

    python3 -I test/lab.py provision a b          # hard-linked copies + BepInEx + the mod
    python3 -I test/lab.py run boot a             # one copy, boot test
    python3 -I test/lab.py run pair host=a client=b --scenario join   # two copies

Rules this follows (coop-anything playbook):
- never write to the owner's install or Steam folders; check them after every run;
- each copy has its own Proton prefix (own saves, own wineserver) and no steamclient.dll, so it
  cannot reach the owner's Steam client; the mod also blocks SteamAPI.Init in test mode;
- copies never open a window on the owner's desktop: each runs inside its own gamescope with the
  headless backend (its own Xwayland, rendered on the GPU through DXVK, nothing presented). The
  launcher refuses to start without gamescope, strips DISPLAY/WAYLAND_DISPLAY/XAUTHORITY, gives each
  copy a private XDG_RUNTIME_DIR (so not even the desktop's Wayland, PipeWire or Pulse sockets are
  reachable), and stops a copy if gamescope does not confirm the headless backend.
  Xvfb does not work for this game (software GL never draws a second frame; NVIDIA can't present);
- gamescope 3.17 segfaults whenever it shuts down by itself (child exit or SIGTERM), which wakes the
  desktop's crash handler. So its child never exits on its own (a wrapper records the game's exit
  code, then sleeps) and the lab ends each copy with SIGKILL on its process group. Core dumps are
  off for the whole tree;
- only our own processes are killed: wineserver -k per prefix, then our process groups, then any
  process still carrying this copy's COOP_LAB_COPY marker.
"""
import hashlib
import json
import os
import resource
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
LOCAL = REPO.parent                      # coop-anything/local
CONF = json.loads((HERE / "lab.json").read_text()) if (HERE / "lab.json").exists() else {}

BASE = Path(CONF.get("base_install", LOCAL / "test-install" / "Planetbase"))
BEPINEX = Path(CONF.get("bepinex", LOCAL / "downloads" / "bepinex-5.4.23.5" / "extracted"))
LAB = Path(CONF.get("lab_dir", LOCAL / "lab"))
PFX_ROOT = Path(CONF.get("prefix_dir", os.environ.get("COOP_PREFIX_DIR", LAB / "prefixes")))
# private XDG_RUNTIME_DIR per copy; short, because Unix socket paths are limited to 107 bytes
RT_ROOT = Path(CONF.get("runtime_dir", f"/tmp/pbcoop-rt-{os.getuid()}"))
PROTON = Path(CONF.get("proton", Path.home() / ".local/share/Steam/steamapps/common/Proton 9.0 (Beta)"))
SNIPER = Path(CONF.get("sniper", Path.home() / ".local/share/Steam/steamapps/common/SteamLinuxRuntime_sniper"))
PROTECTED = CONF.get("protected", {})
PLUGIN = REPO / "out" / "PlanetbaseCoop.dll"


def log(msg):
    print(f"[lab] {msg}", flush=True)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- protection
def dir_manifest(root):
    out = {}
    for dirpath, _, files in os.walk(root):
        for f in files:
            p = os.path.join(dirpath, f)
            try:
                out[os.path.relpath(p, root)] = sha256(p)
            except OSError:
                out[os.path.relpath(p, root)] = "unreadable"
    return out


_WATCH_BEFORE = {}


def owner_game_running():
    inst = PROTECTED.get("owner_install")
    if not inst:
        return False
    try:
        out = subprocess.run(["pgrep", "-af", "[P]lanetbase.exe"], capture_output=True, text=True).stdout
    except OSError:
        return False
    return any(inst in line or ("common/Planetbase" in line and "/lab/inst-" not in line) for line in out.splitlines())


def check_protected():
    """Owner's install hashes unchanged and owner's Planetbase prefix/cloud folders untouched."""
    ok = True
    for d in PROTECTED.get("watch_dirs", []):
        now = dir_manifest(d) if os.path.isdir(d) else {}
        if d not in _WATCH_BEFORE:
            _WATCH_BEFORE[d] = now
        elif now != _WATCH_BEFORE[d]:
            changed = sorted(set(now.items()) ^ set(_WATCH_BEFORE[d].items()))
            log(f"OWNER SAVE FOLDER CHANGED during the run: {d}: {changed[:5]}")
            ok = False
    for path, want in PROTECTED.get("hashes", {}).items():
        got = sha256(path) if os.path.exists(path) else "missing"
        if got != want:
            log(f"PROTECTED CHANGED: {path} {got}")
            ok = False
    for path in PROTECTED.get("must_be_absent", []):
        if os.path.exists(path):
            log(f"PROTECTED PATH APPEARED: {path} (did the owner launch the game, or did a test leak?)")
            ok = False
    log("protected paths OK" if ok else "PROTECTED CHECK FAILED")
    return ok


# ---------------------------------------------------------------- provisioning
def inst_dir(name):
    return LAB / f"inst-{name}"


def pfx_dir(name):
    return PFX_ROOT / f"pfx-{name}"


def write_private(path, data):
    """Replace a file with a private copy (never write through a hard link)."""
    path = Path(path)
    if path.exists() or path.is_symlink():
        path.unlink()
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(data, bytes):
        path.write_bytes(data)
    else:
        path.write_text(data)


def provision(name):
    inst = inst_dir(name)
    LAB.mkdir(parents=True, exist_ok=True)
    if not inst.exists():
        log(f"creating {inst} (hard links to {BASE})")
        subprocess.check_call(["cp", "-al", str(BASE), str(inst)])
    # BepInEx: real copies, so logs/config/cache written by one copy stay private
    bep = inst / "BepInEx"
    if bep.exists():
        shutil.rmtree(bep / "core", ignore_errors=True)
    shutil.copytree(BEPINEX / "BepInEx", bep, dirs_exist_ok=True)
    write_private(inst / "winhttp.dll", (BEPINEX / "winhttp.dll").read_bytes())
    ini = (BEPINEX / "doorstop_config.ini").read_text()
    ini = ini.replace("boot_config_override =", "boot_config_override = coop_boot.config")
    write_private(inst / "doorstop_config.ini", ini)
    # Unity "single-instance" removed in a private boot.config (doorstop override)
    boot = (BASE / "Planetbase_Data" / "boot.config").read_text().splitlines()
    boot = [l for l in boot if not l.startswith("single-instance")]
    write_private(inst / "coop_boot.config", "\n".join(boot) + "\n")
    # steam_appid.txt: RestartAppIfNecessary returns false instead of relaunching through Steam
    write_private(inst / "steam_appid.txt", "403190\n")
    # the mod
    plugins = bep / "plugins"
    plugins.mkdir(parents=True, exist_ok=True)
    if not PLUGIN.exists():
        sys.exit(f"missing {PLUGIN}: run ./build.sh first")
    write_private(plugins / "PlanetbaseCoop.dll", PLUGIN.read_bytes())
    # check nothing we rely on is a hard link we might write through
    for p in [inst / "winhttp.dll", inst / "doorstop_config.ini", inst / "steam_appid.txt", plugins / "PlanetbaseCoop.dll"]:
        assert os.stat(p).st_nlink == 1, p
    log(f"provisioned {inst}")


# ---------------------------------------------------------------- running
def fake_steam_dir():
    d = LAB / "fake-steam"          # no legacycompat/steamclient.dll in here, on purpose
    d.mkdir(parents=True, exist_ok=True)
    return d


def wineserver_kill(name):
    env = dict(os.environ, WINEPREFIX=str(pfx_dir(name) / "pfx"))
    ws = PROTON / "files" / "bin" / "wineserver"
    if ws.exists() and (pfx_dir(name) / "pfx").exists():
        subprocess.call([str(ws), "-k"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


GAMESCOPE = None
# what the owner's desktop session uses; a copy must never see any of these
DESKTOP_ENV = {k: os.environ.get(k) for k in ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR")}


def require_gamescope():
    """Copies only ever run inside a headless gamescope. No gamescope, no run."""
    global GAMESCOPE
    gs = CONF.get("gamescope") or shutil.which("gamescope")
    if not gs or not os.access(gs, os.X_OK):
        sys.exit("refusing to run: gamescope is not installed (test copies must not open windows on the owner's desktop)")
    out = subprocess.run([gs, "--version"], capture_output=True, text=True)
    ver = next((l.split("version", 1)[1].split()[0] for l in (out.stdout + out.stderr).splitlines() if "version" in l), "unknown")
    helps = subprocess.run([gs, "--help"], capture_output=True, text=True)
    if "headless" not in helps.stdout + helps.stderr:
        sys.exit(f"refusing to run: gamescope {ver} has no headless backend")
    GAMESCOPE = gs
    log(f"gamescope {ver} (headless backend)")


def no_core_dumps():
    # child side of Popen: no core files and no crash-handler popups for anything in a copy's tree
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))


def to_windows(path):
    return "Z:" + str(path).replace("/", "\\")


class Copy:
    def __init__(self, name, mode, extra_env=None, width=800, height=600):
        self.name, self.mode = name, mode
        self.run_dir = LAB / "runs" / name
        shutil.rmtree(self.run_dir, ignore_errors=True)
        self.run_dir.mkdir(parents=True)
        self.status = self.run_dir / "status.txt"
        self.coop_log = self.run_dir / "coop.log"
        self.player_log = self.run_dir / "player.log"
        self.launch_log = self.run_dir / "launch.log"     # gamescope + sniper + Proton output
        self.exit_file = self.run_dir / "exit.txt"        # the game's exit code, written by the wrapper
        self.width, self.height = width, height
        self.marker = f"{name}@{LAB}"
        self.runtime = RT_ROOT / name
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(("SteamAppId", "SteamGameId", "SteamOverlay", "STEAM_", "PROTON_", "WINE", "DXVK", "COOP_",
                                    "WAYLAND_DISPLAY", "DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR",
                                    "PULSE_", "PIPEWIRE_", "DBUS_SESSION_BUS_ADDRESS"))}
        env.update({
            # gamescope sets DISPLAY for the game to its own Xwayland; the desktop's is never passed on
            "XDG_RUNTIME_DIR": str(self.runtime),
            "COOP_LAB_COPY": self.marker,
            # GPU rendering is fine inside gamescope; cheap settings keep four copies light
            "COOP_LOWGFX": os.environ.get("COOP_LOWGFX", "1"),
        })
        if os.environ.get("COOP_NORENDER") == "1":
            env["COOP_NORENDER"] = "1"              # optional: cameras draw nothing
        env.update({
            "STEAM_COMPAT_DATA_PATH": str(pfx_dir(name)),
            "STEAM_COMPAT_CLIENT_INSTALL_PATH": str(fake_steam_dir()),
            "WINEDLLOVERRIDES": "winhttp=n,b",
            "WINEDEBUG": "-all",
            "DXVK_LOG_LEVEL": "none",
            # the sniper container only sees folders it is told about (Steam sets these two)
            "STEAM_COMPAT_INSTALL_PATH": str(inst_dir(name)),
            "STEAM_COMPAT_MOUNTS": ":".join([str(LAB), str(PFX_ROOT)]),
            "SteamGameId": "403190",        # what Steam sets; Proton uses it for its log name and game fixes
            "COOP_TEST": mode,
            "COOP_NAME": name,
            "COOP_LOG": to_windows(self.coop_log),
            "COOP_STATUS": to_windows(self.status),
            "COOP_WIDTH": str(width),
            "COOP_HEIGHT": str(height),
        })
        if os.environ.get("COOP_PROTON_LOG"):
            env.update({"PROTON_LOG": "1", "PROTON_LOG_DIR": str(self.run_dir)})
            env.pop("WINEDEBUG"); env.pop("DXVK_LOG_LEVEL")
        env.update(extra_env or {})
        self.env = env
        self.proc = None

    def start(self):
        pfx_dir(self.name).mkdir(parents=True, exist_ok=True)
        shutil.rmtree(self.runtime, ignore_errors=True)
        self.runtime.mkdir(parents=True, mode=0o700)
        os.chmod(RT_ROOT, 0o700)
        if len(str(self.runtime)) > 80:
            sys.exit(f"runtime dir path too long for sockets: {self.runtime}")
        exe = inst_dir(self.name) / "Planetbase.exe"
        # Proton 9 must run inside the Steam Linux Runtime (sniper) container, as Steam does:
        # run bare on this host the game hangs single-threaded right after winex11.drv loads.
        game = [str(SNIPER / "_v2-entry-point"), "--verb=waitforexitandrun", "--",
                str(PROTON / "proton"), "waitforexitandrun", str(exe),
                "-screen-fullscreen", "0", "-screen-width", str(self.width), "-screen-height", str(self.height),
                "-logFile", to_windows(self.player_log)]
        # gamescope's child must not exit by itself (gamescope would segfault): record and sleep
        wrapper = 'echo "child DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY"; "$@"; echo $? > "$COOP_EXIT_FILE"; exec sleep 86400'
        args = [GAMESCOPE, "--backend", "headless",
                "-w", str(self.width), "-h", str(self.height), "-W", str(self.width), "-H", str(self.height),
                "-r", "30", "--", "sh", "-c", wrapper, "sh"] + game
        env = dict(self.env, COOP_EXIT_FILE=str(self.exit_file))
        log(f"start {self.name} mode={self.mode} (gamescope headless)")
        with open(self.launch_log, "w") as out:
            # cwd must NOT be the game folder: inside the container that gives c0000135 (DLL not found)
            self.proc = subprocess.Popen(args, env=env, cwd=str(LAB), stdout=out, stderr=subprocess.STDOUT,
                                         start_new_session=True, preexec_fn=no_core_dumps)
        if not self.confirm_headless():
            self.stop()
            sys.exit(f"refusing to continue: gamescope for {self.name} did not confirm a headless backend; see {self.launch_log}")

    def confirm_headless(self):
        """gamescope must say it started the headless backend, and the wrapper must see its own DISPLAY."""
        def seen():
            t = self.launch_log.read_text(errors="replace") if self.launch_log.exists() else ""
            return "Starting headless backend" in t and "child DISPLAY=" in t
        if not wait_for(lambda: seen() or not self.alive(), 30) or not seen():
            log(f"{self.name}: no headless confirmation in launch.log")
            return False
        text = self.launch_log.read_text(errors="replace")
        line = next(l for l in text.splitlines() if l.startswith("child DISPLAY="))
        disp = line.split()[1].split("=", 1)[1]
        wl = line.split("WAYLAND_DISPLAY=", 1)[1].strip()
        if not disp or disp == DESKTOP_ENV["DISPLAY"] or wl:
            log(f"{self.name}: child sees DISPLAY={disp!r} WAYLAND_DISPLAY={wl!r} (desktop has {DESKTOP_ENV['DISPLAY']!r})")
            return False
        self.display = disp
        log(f"{self.name}: headless gamescope, game display {disp}")
        return True

    def lines(self):
        try:
            return self.status.read_text(errors="replace").splitlines()
        except FileNotFoundError:
            return []

    def result(self):
        for l in self.lines():
            if l.startswith("RESULT:"):
                return l
        return None

    def alive(self):
        """The game is running: gamescope is up and the wrapper has not recorded an exit code."""
        return self.proc is not None and self.proc.poll() is None and not self.exit_file.exists()

    def exit_code(self):
        try:
            return self.exit_file.read_text().strip()
        except FileNotFoundError:
            return None if self.proc is None or self.proc.poll() is None else f"gamescope died ({self.proc.returncode})"

    def stop(self):
        wineserver_kill(self.name)
        if self.proc and self.proc.poll() is None:
            # SIGKILL only: gamescope segfaults on any other way out
            try:
                os.killpg(self.proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            self.proc.wait(10)
        kill_marked(self.marker)
        shutil.rmtree(self.runtime, ignore_errors=True)


def marked_pids(marker):
    want = f"COOP_LAB_COPY={marker}".encode()
    pids = []
    for pid in os.listdir("/proc"):
        if not pid.isdigit() or int(pid) == os.getpid():
            continue
        try:
            with open(f"/proc/{pid}/environ", "rb") as f:
                if want in f.read().split(b"\0"):
                    pids.append(int(pid))
        except OSError:
            pass
    return pids


def kill_marked(marker):
    """Anything left from this copy (it carries our marker in its environment)."""
    for _ in range(3):
        pids = marked_pids(marker)
        if not pids:
            return
        for p in pids:
            try:
                os.kill(p, signal.SIGKILL)
            except ProcessLookupError:
                pass
        time.sleep(0.5)
    left = marked_pids(marker)
    if left:
        log(f"WARNING: processes of {marker} still alive: {left}")


def tail(path, n=25):
    try:
        return "\n".join(Path(path).read_text(errors="replace").splitlines()[-n:])
    except FileNotFoundError:
        return "(missing)"


def run_copies(copies, timeout):
    if owner_game_running():
        sys.exit("refusing to run: the owner's own Planetbase is running")
    if not check_protected():
        sys.exit("refusing to run: protected check failed before start")
    for c in copies:
        wineserver_kill(c.name)
    t0 = time.time()
    try:
        for i, c in enumerate(copies):
            c.start()
            if i + 1 < len(copies):
                # let the first copy reach its menu before the next starts (CPU, shader cache)
                wait_for(lambda: any("READY" in l or "RESULT:" in l for l in c.lines()) or not c.alive(), 420)
        while time.time() - t0 < timeout:
            res = [c.result() for c in copies]
            if all(res):
                break
            if any(r and "FAIL" in r for r in res):
                break
            dead = [c for c in copies if not c.alive() and not c.result()]
            if dead:
                time.sleep(3)
                if not dead[0].result():
                    log(f"{dead[0].name} exited without a result (exit code {dead[0].exit_code()})")
                    break
            time.sleep(1)
    finally:
        for c in copies:
            c.stop()
    results = {c.name: (c.result() or ("RESULT: TIMEOUT" if time.time() - t0 >= timeout else "RESULT: FAIL no result line")) for c in copies}
    for c in copies:
        log(f"--- {c.name}: {results[c.name]}")
        log(f"status:\n{tail(c.status, 40)}")
        if "PASS" not in results[c.name]:
            log(f"coop.log tail:\n{tail(c.coop_log)}")
            log(f"player.log tail:\n{tail(c.player_log)}")
            log(f"launch.log tail:\n{tail(c.launch_log, 15)}")
    protected_ok = check_protected()
    passed = all("PASS" in r for r in results.values()) and protected_ok
    print("RESULT: PASS" if passed else "RESULT: FAIL", flush=True)
    return passed


def wait_for(cond, timeout):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if cond():
            return True
        time.sleep(0.5)
    return False


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    cmd = argv[1]
    if cmd == "provision":
        for n in argv[2:]:
            provision(n)
        return 0
    if cmd == "check":
        return 0 if check_protected() else 1
    if cmd == "run":
        what = argv[2]
        opts = dict(a.split("=", 1) for a in argv[3:] if "=" in a)
        names = [a for a in argv[3:] if "=" not in a]
        timeout = int(opts.pop("timeout", "240"))
        require_gamescope()
        if what == "boot":
            ok = run_copies([Copy(names[0], "boot")], timeout)
        elif what == "pair":
            scen = opts.pop("scenario", "join")
            extra = {"COOP_SCENARIO": scen, "COOP_PORT": opts.pop("port", "47610")}
            if "mut" in opts:
                extra["COOP_MUT"] = opts.pop("mut")
            client_extra = dict(extra)
            if "fake_mod" in opts:
                client_extra["COOP_FAKE_MOD"] = opts.pop("fake_mod")
            host = Copy(opts["host"], "host", extra)
            client = Copy(opts["client"], "client", client_extra)
            ok = run_copies([host, client], timeout)
        else:
            sys.exit(f"unknown run target {what}")
        return 0 if ok else 1
    sys.exit(f"unknown command {cmd}")


if __name__ == "__main__":
    sys.exit(main(sys.argv))
