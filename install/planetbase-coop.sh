#!/usr/bin/env bash
# Planetbase Co-op : installation, mise à jour et désinstallation pour Linux (Steam + Proton).
#
#   bash planetbase-coop.sh               menu (installer / mettre à jour, désinstaller)
#   bash planetbase-coop.sh install       installe ou met à jour sans menu
#   bash planetbase-coop.sh uninstall     désinstalle
#   Options : --game-dir <dossier>  --yes (répond par défaut à tout)  --no-launch  --no-self-update
#
# Ce que fait le script : trouve Steam et Planetbase, vérifie la version du jeu, installe BepInEx
# (archive officielle, empreinte SHA-256 vérifiée), installe ou remplace le mod par la dernière
# version publiée (empreinte vérifiée), règle l'option de lancement Steam nécessaire sous Proton,
# puis propose de lancer le jeu. Il ne touche qu'au dossier du jeu et, pour l'option de lancement,
# au fichier localconfig.vdf de Steam (sauvegardé avant).
set -uo pipefail

INSTALLER_VERSION=1
APP_ID=403190
SOURCE="${COOP_SOURCE:-https://raw.githubusercontent.com/Elwynn18/Planetbase-coop/main}"
PAGE="https://github.com/Elwynn18/Planetbase-coop"
BEPINEX_URL="${COOP_BEPINEX_URL:-https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip}"
BEPINEX_SHA="37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82"
OUR_OPTION='WINEDLLOVERRIDES="winhttp=n,b" %command%'
LOG="${XDG_CACHE_HOME:-$HOME/.cache}/planetbase-coop-installer.log"

ORIG_ARGS=("$@")
ACTION=""; GAME_DIR=""; ASSUME_YES=0; NO_LAUNCH=0; NO_SELF_UPDATE=0
while [ $# -gt 0 ]; do
  case "$1" in
    install|update) ACTION=install ;;
    uninstall) ACTION=uninstall ;;
    --game-dir) GAME_DIR="${2:-}"; shift ;;
    --yes|-y) ASSUME_YES=1 ;;
    --no-launch) NO_LAUNCH=1 ;;
    --no-self-update) NO_SELF_UPDATE=1 ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Argument inconnu : $1 (voir --help)"; exit 2 ;;
  esac
  shift
done
[ -t 0 ] || ASSUME_YES=1   # pas de clavier : réponses par défaut

# ------------------------------------------------------------------ affichage
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  B=$'\e[1m'; D=$'\e[2m'; R=$'\e[0m'; RED=$'\e[31m'; GRN=$'\e[32m'; YEL=$'\e[33m'; BLU=$'\e[34m'; MAG=$'\e[35m'; CYN=$'\e[36m'
else
  B=""; D=""; R=""; RED=""; GRN=""; YEL=""; BLU=""; MAG=""; CYN=""
fi
W=74                     # largeur des cadres
STEP=0; STEPS=6
RESULTS=()               # lignes du résumé final

mkdir -p "$(dirname "$LOG")" 2>/dev/null
: > "$LOG" 2>/dev/null || LOG=/dev/null
logf() { printf '%s\n' "$*" | sed 's/\x1b\[[0-9;]*m//g' >> "$LOG"; }
say()  { printf '%s\n' "$*"; logf "$*"; }

rep() { local s="" i; for ((i = 0; i < $2; i++)); do s+="$1"; done; printf '%s' "$s"; }

# longueur affichée (sans les codes couleur)
vlen() { local t; t=$(printf '%s' "$1" | sed 's/\x1b\[[0-9;]*m//g'); printf '%s' "${#t}"; }

# chemin raccourci pour l'affichage : ~ pour le dossier personnel, « … » au milieu si trop long
short() {
  local p="$1" max="${2:-52}"
  [[ "$p" == "$HOME"* ]] && p="~${p#"$HOME"}"
  if [ ${#p} -gt "$max" ]; then p="${p:0:16}…${p: -$((max - 17))}"; fi
  printf '%s' "$p"
}

boxline() {  # boxline <couleur> <texte>
  local pad=$((W - 4 - $(vlen "$2"))); [ $pad -lt 0 ] && pad=0
  say "$1│$R $2$(rep ' ' $pad) $1│$R"
}

banner() {
  say ""
  say "${CYN}╭$(rep '─' $((W - 2)))╮$R"
  boxline "$CYN" ""
  boxline "$CYN" "${B}✦  PLANETBASE CO-OP  ✦$R"
  boxline "$CYN" "${D}Installation · mise à jour · désinstallation  (Linux)$R"
  boxline "$CYN" ""
  say "${CYN}╰$(rep '─' $((W - 2)))╯$R"
}

step() {
  STEP=$((STEP + 1))
  local title="[$STEP/$STEPS] $1"
  say ""
  say "${B}${BLU}━━ $title $(rep '━' $((W - 4 - ${#title})))$R"
}

ok()   { say "  ${GRN}✔$R $*"; }
warn() { say "  ${YEL}⚠$R $*"; }
bad()  { say "  ${RED}✖$R $*"; }
info() { say "  ${D}•$R $*"; }
sub()  { say "    ${D}$*$R"; }

# cadre d'aide pour ce que le script ne peut pas faire seul
helpbox() {  # helpbox <titre> <ligne>...
  local title="$1"; shift
  say ""
  say "  ${YEL}┌─ ${B}$title$R${YEL} $(rep '─' $((W - 8 - ${#title})))┐$R"
  local l
  for l in "$@"; do
    local pad=$((W - 6 - $(vlen "$l"))); [ $pad -lt 0 ] && pad=0
    say "  ${YEL}│$R $l$(rep ' ' $pad)${YEL}│$R"
  done
  say "  ${YEL}└$(rep '─' $((W - 5)))┘$R"
}

ask() {  # ask <question> <o|n par défaut> : 0 = oui
  local q="$1" def="$2" hint ans
  if [ "$def" = o ]; then hint="[O/n]"; else hint="[o/N]"; fi
  if [ $ASSUME_YES = 1 ]; then say "  ${MAG}?$R $q $hint ${D}→ $def (automatique)$R"; [ "$def" = o ]; return; fi
  printf '  %s?%s %s %s ' "$MAG" "$R" "$q" "$hint"
  read -r ans || ans=""
  logf "  ? $q $hint $ans"
  ans="${ans,,}"
  [ -z "$ans" ] && ans="$def"
  [[ "$ans" == o* || "$ans" == y* ]]
}

pause_end() { [ $ASSUME_YES = 1 ] && return; printf '\n  %sAppuyez sur Entrée pour fermer.%s ' "$D" "$R"; read -r _ || true; }

die() {  # die <message> [lignes d'aide...]
  bad "$1"; shift
  [ $# -gt 0 ] && helpbox "Que faire" "$@"
  say ""
  say "  ${D}Journal de cette exécution : $(short "$LOG" 60)$R"
  pause_end
  exit 1
}

spin_wait() {  # spin_wait <message> <commande qui réussit quand c'est fini> [délai max s]
  local msg="$1" cond="$2" max="${3:-0}" i=0 frames='⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏'
  while ! eval "$cond"; do
    [ -t 1 ] && printf '\r  %s%s%s %s ' "$CYN" "${frames:$((i % 10)):1}" "$R" "$msg"
    sleep 0.5; i=$((i + 1))
    if [ "$max" -gt 0 ] && [ $i -ge $((max * 2)) ]; then [ -t 1 ] && printf '\r\e[K'; return 1; fi
  done
  [ -t 1 ] && printf '\r\e[K'
  return 0
}

# ------------------------------------------------------------------ outils
TMP=$(mktemp -d "${TMPDIR:-/tmp}/planetbase-coop.XXXXXX")
trap 'rm -rf "$TMP"' EXIT

have() { command -v "$1" >/dev/null 2>&1; }
sha() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }

fetch() {  # fetch <url ou chemin local> <destination> [progress]
  local src="$1" dst="$2"
  if [ -e "$src" ]; then cp -f "$src" "$dst"; return; fi
  case "$src" in file://*) cp -f "${src#file://}" "$dst"; return ;; esac
  if have curl; then
    if [ "${3:-}" = progress ] && [ -t 1 ]; then curl -fL --progress-bar --retry 2 -o "$dst" "$src"
    else curl -fsSL --retry 2 -o "$dst" "$src"; fi
  else
    if [ "${3:-}" = progress ] && [ -t 1 ]; then wget -q --show-progress -O "$dst" "$src"
    else wget -q -O "$dst" "$src"; fi
  fi
}

unzip_to() {  # unzip_to <zip> <dossier>
  if have unzip; then unzip -o -q "$1" -d "$2"
  elif have bsdtar; then bsdtar -xf "$1" -C "$2"
  else python3 -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$1" "$2"; fi
}

pe_arch() {  # architecture d'un .dll Windows : x86, x64 ou ?
  local off m
  off=$(od -An -tu4 -j60 -N4 "$1" 2>/dev/null | tr -d ' ') || { echo "?"; return; }
  m=$(od -An -tx2 -j$((off + 4)) -N2 "$1" 2>/dev/null | tr -d ' ')
  case "$m" in 014c) echo x86 ;; 8664) echo x64 ;; *) echo "?" ;; esac
}

steam_running() {
  if [ -n "${COOP_FAKE_STEAM_RUNNING:-}" ]; then [ "$COOP_FAKE_STEAM_RUNNING" = 1 ]; return; fi
  pgrep -x steam >/dev/null 2>&1 || pgrep -x steamwebhelper >/dev/null 2>&1
}

game_running() {
  if [ -n "${COOP_FAKE_GAME_RUNNING:-}" ]; then [ "$COOP_FAKE_GAME_RUNNING" = 1 ]; return; fi
  pgrep -f 'Planetbase\.exe' >/dev/null 2>&1
}

open_url() {
  if [ -n "${COOP_FAKE_STEAM_RUNNING:-}" ]; then info "(test) ouverture de $1"; return; fi
  if have xdg-open; then nohup xdg-open "$1" >/dev/null 2>&1 &
  elif have steam; then nohup steam "$1" >/dev/null 2>&1 &
  fi
  disown 2>/dev/null || true
}

# ------------------------------------------------------------------ manifeste (dernière version)
declare -A MAN=()
read_manifest() {
  if ! fetch "$SOURCE/release/latest.txt" "$TMP/latest.txt" 2>>"$LOG"; then return 1; fi
  local k v
  while IFS='=' read -r k v; do
    [[ -z "$k" || "$k" == \#* ]] && continue
    MAN["$k"]="${v%$'\r'}"
  done < "$TMP/latest.txt"
  [ -n "${MAN[version]:-}" ] && [ -n "${MAN[dll]:-}" ] && [ -n "${MAN[sha256]:-}" ]
}

self_update() {
  [ $NO_SELF_UPDATE = 1 ] && return
  local latest="${MAN[installer_version]:-0}" want="${MAN[installer_sh_sha256]:-}" path="${MAN[installer_sh]:-}"
  [ "$latest" -gt "$INSTALLER_VERSION" ] 2>/dev/null || return
  [ -n "$want" ] && [ -n "$path" ] || return
  info "Une nouvelle version de ce script existe (v$latest). Mise à jour du script…"
  if fetch "$SOURCE/$path" "$TMP/new.sh" 2>>"$LOG" && [ "$(sha "$TMP/new.sh")" = "$want" ]; then
    if cp -f "$TMP/new.sh" "$0" 2>/dev/null; then
      ok "Script mis à jour, il redémarre."
      exec bash "$0" "${ORIG_ARGS[@]}" --no-self-update
    fi
  fi
  warn "Impossible de mettre le script à jour tout seul ; on continue avec celui-ci."
}

# ------------------------------------------------------------------ Steam et le jeu
STEAM_ROOT=""; LIBS=()
add_lib() { local l; for l in "${LIBS[@]}"; do [ "$l" = "$1" ] && return; done; LIBS+=("$1"); }
find_steam() {
  local c seen="" cands
  if [ -n "${STEAM_ROOT_OVERRIDE:-}" ]; then cands=("$STEAM_ROOT_OVERRIDE")   # tests
  else cands=("$HOME/.steam/steam" "$HOME/.local/share/Steam" \
              "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam"); fi
  for c in "${cands[@]}"; do
    [ -d "$c/steamapps" ] || continue
    c=$(readlink -f "$c")
    [[ " $seen " == *" $c "* ]] && continue
    seen="$seen $c"
    [ -z "$STEAM_ROOT" ] && STEAM_ROOT="$c"
    add_lib "$c"
    local vdf="$c/steamapps/libraryfolders.vdf" p
    if [ -f "$vdf" ]; then
      while IFS= read -r p; do
        p="${p//\\\\/\\}"
        [ -d "$p/steamapps" ] && add_lib "$(readlink -f "$p")"
      done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf")
    fi
  done
  [ -n "$STEAM_ROOT" ]
}

find_game() {
  [ -n "$GAME_DIR" ] && return 0
  local l acf dir
  for l in "${LIBS[@]}"; do
    acf="$l/steamapps/appmanifest_$APP_ID.acf"
    [ -f "$acf" ] || continue
    dir=$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$acf" | head -1)
    [ -n "$dir" ] && [ -d "$l/steamapps/common/$dir" ] && { GAME_DIR="$l/steamapps/common/$dir"; return 0; }
  done
  return 1
}

# ------------------------------------------------------------------ option de lancement (localconfig.vdf)
VDF_TOOL="$TMP/vdf.py"
cat > "$VDF_TOOL" <<'PY'
# status | apply | remove  <localconfig.vdf> <appid>
# Edits only the LaunchOptions value of one app, in place; the rest of the file is left byte for byte.
import os, re, sys
mode, path, app = sys.argv[1], sys.argv[2], sys.argv[3]
OURS = 'WINEDLLOVERRIDES="winhttp=n,b"'
text = open(path, encoding="utf-8", errors="surrogateescape").read()
toks = list(re.finditer(r'"((?:[^"\\]|\\.)*)"|([{}])', text))
target = ["userlocalconfigstore", "software", "valve", "steam", "apps"]
stack, info, k = [], {}, 0
while k < len(toks):
    m = toks[k]
    if m.group(2) == "}":
        if not stack: break
        low = [s.lower() for s in stack]
        if low == target + [app]: info["app_close"] = m.start()
        if low == target: info["apps_close"] = m.start()
        stack.pop(); k += 1; continue
    if m.group(2) == "{": k += 1; continue
    if k + 1 >= len(toks): break
    n = toks[k + 1]
    if n.group(2) == "{":
        stack.append(m.group(1)); k += 2; continue
    if [s.lower() for s in stack] == target + [app] and m.group(1).lower() == "launchoptions":
        info["lo"] = (n.start(1), n.end(1), n.group(1))
    k += 2
unesc = lambda s: s.replace('\\"', '"').replace("\\\\", "\\")
esc = lambda s: s.replace("\\", "\\\\").replace('"', '\\"')
cur = unesc(info["lo"][2]) if "lo" in info else ""

WDO = re.compile(r'WINEDLLOVERRIDES=("([^"]*)"|([^\s"]*))')

def merged(c):
    c = c.strip()
    if "winhttp=n,b" in c: return None
    if "WINEDLLOVERRIDES" in c:   # already a list of overrides: add ours to it
        m = WDO.search(c)
        if not m: return "CONFLICT"
        lst = m.group(2) if m.group(2) is not None else m.group(3)
        lst = (lst + ";" if lst else "") + "winhttp=n,b"
        return c[:m.start()] + 'WINEDLLOVERRIDES="' + lst + '"' + c[m.end():]
    if not c: return OURS + " %command%"
    if "%command%" in c: return OURS + " " + c
    return OURS + " %command% " + c

def unmerged(c):
    c = c.strip()
    m = WDO.search(c)
    if m and not c.startswith(OURS):   # ours was added to an existing list
        lst = m.group(2) if m.group(2) is not None else m.group(3)
        rest = ";".join(x for x in lst.split(";") if x and x != "winhttp=n,b")
        return c[:m.start()] + ('WINEDLLOVERRIDES="' + rest + '"' if rest else "") + c[m.end():]
    if c.startswith(OURS):
        c = c[len(OURS):].strip()
        if c == "%command%": c = ""
    return c

def write(new):
    global text
    if "lo" in info:
        a, b, _ = info["lo"]; text = text[:a] + esc(new) + text[b:]
    elif "app_close" in info:
        pos = info["app_close"]; ls = text.rfind("\n", 0, pos) + 1; ind = text[ls:pos]
        text = text[:ls] + ind + '\t"LaunchOptions"\t\t"' + esc(new) + '"\n' + text[ls:]
    elif "apps_close" in info:
        pos = info["apps_close"]; ls = text.rfind("\n", 0, pos) + 1; ind = text[ls:pos]
        block = ind + '\t"' + app + '"\n' + ind + "\t{\n" + ind + '\t\t"LaunchOptions"\t\t"' + esc(new) + '"\n' + ind + "\t}\n"
        text = text[:ls] + block + text[ls:]
    else:
        print("error:structure"); sys.exit(3)
    tmp = path + ".coop-tmp"
    with open(tmp, "w", encoding="utf-8", errors="surrogateescape", newline="") as f: f.write(text)
    os.replace(tmp, path)

if mode == "status":
    m = merged(cur)
    print("ok" if m is None else ("conflict" if m == "CONFLICT" else "needs"))
    print(cur)
elif mode == "apply":
    m = merged(cur)
    if m is None: print("ok")
    elif m == "CONFLICT": print("conflict"); sys.exit(2)
    else: write(m); print("done")
elif mode == "remove":
    u = unmerged(cur)
    if u == cur.strip(): print("ok")
    else: write(u); print("done")
PY

vdf_files() { ls "$STEAM_ROOT"/userdata/*/config/localconfig.vdf 2>/dev/null; }

close_steam() {
  steam_running || return 0
  say ""
  info "Steam est ouvert. Il doit être fermé pendant ce réglage, sinon il l'efface en quittant."
  ask "Je ferme Steam maintenant (il sera relancé avec le jeu) ?" o || return 1
  if [ -z "${COOP_FAKE_STEAM_RUNNING:-}" ]; then
    if have steam; then nohup steam -shutdown >/dev/null 2>&1 & else open_url "steam://exit"; fi
  else
    COOP_FAKE_STEAM_RUNNING=0
  fi
  if spin_wait "Fermeture de Steam…" '! steam_running' 60; then ok "Steam fermé."; return 0; fi
  warn "Steam ne s'est pas fermé dans la minute."
  return 1
}

# ------------------------------------------------------------------ étapes
do_checks() {
  step "Vérifications"
  local missing=()
  have curl || have wget || missing+=("curl")
  have sha256sum || missing+=("coreutils (sha256sum)")
  have unzip || have bsdtar || have python3 || missing+=("unzip")
  have od || missing+=("coreutils (od)")
  if [ ${#missing[@]} -gt 0 ]; then
    die "Il manque des outils : ${missing[*]}" \
        "Installez-les avec le gestionnaire de paquets, par exemple :" \
        "  Arch : sudo pacman -S ${missing[*]%% *}" \
        "  Debian/Ubuntu : sudo apt install ${missing[*]%% *}" \
        "puis relancez ce script."
  fi
  ok "Outils présents ${D}($(have curl && echo curl || echo wget), sha256sum, $(have unzip && echo unzip || (have bsdtar && echo bsdtar) || echo python3))$R"
  if read_manifest; then
    ok "Dernière version du mod : ${B}${MAN[version]}$R ${D}(jeu attendu : Planetbase ${MAN[game_version]:-1.4.2})$R"
    self_update
  else
    die "Impossible de lire la dernière version du mod sur GitHub." \
        "Vérifiez la connexion Internet, puis relancez le script." \
        "Page du projet : $PAGE"
  fi
}

do_find() {
  step "Recherche de Steam et de Planetbase"
  if [ -n "$GAME_DIR" ]; then
    [ -d "$GAME_DIR" ] || die "Le dossier indiqué n'existe pas : $GAME_DIR"
    GAME_DIR=$(readlink -f "$GAME_DIR")
    find_steam || true
    ok "Dossier du jeu (indiqué) : $(short "$GAME_DIR" 50)"
  else
    if ! find_steam; then
      die "Steam est introuvable." \
          "1. Installez Steam (paquet « steam » de votre distribution, ou Flatpak)." \
          "2. Connectez-vous, installez Planetbase, puis relancez ce script."
    fi
    ok "Steam : $(short "$STEAM_ROOT" 60)"
    [ ${#LIBS[@]} -gt 1 ] && info "${#LIBS[@]} bibliothèques Steam examinées"
    if ! find_game; then
      bad "Planetbase n'est installé dans aucune bibliothèque Steam."
      if ask "J'ouvre la page d'installation de Planetbase dans Steam ?" o; then
        open_url "steam://install/$APP_ID"
        helpbox "Ensuite" "1. Dans la fenêtre Steam, lancez l'installation." "2. Attendez la fin du téléchargement." "3. Relancez ce script."
      fi
      pause_end; exit 1
    fi
    ok "Planetbase : $(short "$GAME_DIR" 60)"
  fi

  if [ ! -f "$GAME_DIR/Planetbase.exe" ]; then
    die "Ce dossier contient la version Linux du jeu ; le mod a besoin de la version Windows (via Proton)." \
        "1. Steam : clic droit sur Planetbase > Propriétés > Compatibilité." \
        "2. Cochez « Forcer l'utilisation d'un outil de compatibilité »" \
        "   et choisissez ${B}Proton 9.0$R (ou plus récent)." \
        "3. Steam télécharge la version Windows : attendez la fin." \
        "4. Relancez ce script."
  fi

  local want="${MAN[game_sha256]:-}" have_sha
  have_sha=$(sha "$GAME_DIR/Planetbase_Data/Managed/Assembly-CSharp.dll")
  if [ -z "$have_sha" ]; then
    die "Fichiers du jeu incomplets (Assembly-CSharp.dll introuvable)." \
        "Steam : clic droit sur Planetbase > Propriétés > Fichiers installés" \
        "> Vérifier l'intégrité des fichiers du jeu, puis relancez ce script."
  elif [ -n "$want" ] && [[ ",$want," != *",$have_sha,"* ]]; then
    warn "Votre jeu n'est pas la version ${MAN[game_version]:-1.4.2} attendue (ou un mod a modifié ses fichiers)."
    helpbox "Pourquoi c'est important" \
      "Le mod refuse de connecter deux joueurs dont les jeux diffèrent." \
      "Pour remettre les fichiers d'origine : Steam > clic droit sur" \
      "Planetbase > Propriétés > Fichiers installés > Vérifier l'intégrité."
    ask "Continuer quand même ?" n || { pause_end; exit 1; }
  else
    ok "Version du jeu : ${MAN[game_version]:-1.4.2} ${D}(fichiers d'origine)$R"
  fi

  if game_running; then
    warn "Planetbase est en cours d'exécution."
    spin_wait "Fermez Planetbase pour continuer… (Ctrl+C pour annuler)" '! game_running' 0
    ok "Planetbase fermé."
  fi
  [ -w "$GAME_DIR" ] || die "Le dossier du jeu n'est pas modifiable par votre utilisateur : $GAME_DIR" \
      "Vérifiez ses droits (ls -ld), ou indiquez un autre dossier avec --game-dir."
}

do_bepinex() {
  step "BepInEx (chargeur de mods)"
  local core="$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" proxy="$GAME_DIR/winhttp.dll" need=0 arch
  if [ -f "$core" ] && [ -f "$proxy" ]; then
    arch=$(pe_arch "$proxy")
    if [ "$arch" = x64 ]; then
      warn "BepInEx installé est la version 64 bits ; Planetbase a besoin de la 32 bits. Remplacement."
      need=1
    else
      ok "BepInEx déjà installé ${D}(32 bits)$R"
    fi
  else
    info "BepInEx n'est pas installé."
    need=1
  fi
  if [ $need = 1 ]; then
    say "  ${CYN}↓$R Téléchargement de BepInEx 5.4.23.5 (32 bits, archive officielle)…"
    fetch "$BEPINEX_URL" "$TMP/bepinex.zip" progress || die "Téléchargement de BepInEx impossible." \
        "Vérifiez la connexion Internet, puis relancez le script."
    [ "$(sha "$TMP/bepinex.zip")" = "$BEPINEX_SHA" ] || die "L'archive BepInEx reçue n'a pas la bonne empreinte : rien n'a été installé." \
        "Relancez le script ; si cela se reproduit, signalez-le sur $PAGE"
    ok "Archive vérifiée ${D}(SHA-256 ${BEPINEX_SHA:0:12}…)$R"
    unzip_to "$TMP/bepinex.zip" "$GAME_DIR" 2>>"$LOG" || die "Décompression de BepInEx impossible."
    ok "BepInEx installé"
  fi
  mkdir -p "$GAME_DIR/BepInEx/plugins"
  local ini="$GAME_DIR/doorstop_config.ini"
  if [ -f "$ini" ] && grep -Eq '^[[:space:]]*enabled[[:space:]]*=[[:space:]]*false' "$ini"; then
    cp -f "$ini" "$ini.bak"
    sed -i -E 's/^([[:space:]]*enabled[[:space:]]*=[[:space:]]*)false/\1true/' "$ini"
    ok "BepInEx était désactivé (doorstop_config.ini) : réactivé ${D}(copie : doorstop_config.ini.bak)$R"
  fi
  RESULTS+=("BepInEx|prêt (32 bits)")
}

do_mod() {
  step "Le mod Planetbase Co-op"
  local dst="$GAME_DIR/BepInEx/plugins/PlanetbaseCoop.dll" verfile="$GAME_DIR/BepInEx/plugins/PlanetbaseCoop.version"
  local new="${MAN[version]}" old="" oldsha=""
  [ -f "$dst" ] && oldsha=$(sha "$dst")
  [ -f "$verfile" ] && old=$(head -1 "$verfile" | tr -d '\r\n')
  if [ "$oldsha" = "${MAN[sha256]}" ]; then
    ok "Déjà à jour : version ${B}$new$R"
    printf '%s\n' "$new" > "$verfile"
    RESULTS+=("Mod|version $new (déjà à jour)")
    return
  fi
  if [ -n "$oldsha" ]; then info "Version installée : ${old:-inconnue} → mise à jour vers ${B}$new$R"
  else info "Première installation du mod (version ${B}$new$R)"; fi
  say "  ${CYN}↓$R Téléchargement du mod…"
  fetch "$SOURCE/${MAN[dll]}" "$TMP/PlanetbaseCoop.dll" progress || die "Téléchargement du mod impossible." \
      "Vérifiez la connexion Internet, puis relancez le script."
  if [ "$(sha "$TMP/PlanetbaseCoop.dll")" != "${MAN[sha256]}" ]; then
    die "Le fichier du mod reçu n'a pas la bonne empreinte : rien n'a été remplacé." \
        "GitHub vient peut-être d'être mis à jour : réessayez dans 5 minutes." \
        "Si cela se reproduit, signalez-le sur $PAGE"
  fi
  ok "Fichier vérifié ${D}(SHA-256 ${MAN[sha256]:0:12}…)$R"
  if [ -n "$oldsha" ]; then
    mkdir -p "$GAME_DIR/BepInEx/coop-backup"
    cp -f "$dst" "$GAME_DIR/BepInEx/coop-backup/PlanetbaseCoop-${old:-ancienne}.dll"
    sub "ancienne version gardée dans BepInEx/coop-backup/"
  fi
  cp -f "$TMP/PlanetbaseCoop.dll" "$dst" || die "Impossible d'écrire $dst"
  printf '%s\n' "$new" > "$verfile"
  if [ -n "$oldsha" ]; then ok "Mod mis à jour : ${old:-ancienne version} → ${B}$new$R"; RESULTS+=("Mod|mis à jour : ${old:-ancienne} → $new")
  else ok "Mod installé : version ${B}$new$R"; RESULTS+=("Mod|installé : version $new"); fi
}

do_launch_option() {
  step "Option de lancement Steam (Proton)"
  if [ -z "$STEAM_ROOT" ]; then
    warn "Steam non trouvé : réglez l'option vous-même."
    manual_option_help; RESULTS+=("Option de lancement|à régler à la main"); return
  fi
  local files f st cur needs=() conflicts=()
  mapfile -t files < <(vdf_files)
  if [ ${#files[@]} -eq 0 ]; then
    warn "Aucun compte Steam trouvé dans $STEAM_ROOT/userdata."
    manual_option_help; RESULTS+=("Option de lancement|à régler à la main"); return
  fi
  if ! have python3; then
    warn "python3 absent : je ne peux pas modifier la configuration Steam."
    manual_option_help; RESULTS+=("Option de lancement|à régler à la main"); return
  fi
  for f in "${files[@]}"; do
    st=$(python3 "$VDF_TOOL" status "$f" "$APP_ID" 2>>"$LOG" | head -1)
    case "$st" in
      ok) ;;
      needs) needs+=("$f") ;;
      *) conflicts+=("$f") ;;
    esac
  done
  if [ ${#needs[@]} -eq 0 ] && [ ${#conflicts[@]} -eq 0 ]; then
    ok "Déjà réglée : ${D}$OUR_OPTION$R"
    RESULTS+=("Option de lancement|déjà réglée"); return
  fi
  if [ ${#needs[@]} -gt 0 ]; then
    info "Sous Proton, Steam doit charger BepInEx : il faut l'option ${B}$OUR_OPTION$R"
    if close_steam; then
      local stamp; stamp=$(date +%Y%m%d-%H%M%S)
      for f in "${needs[@]}"; do
        cp -f "$f" "$f.planetbase-coop-$stamp.bak"
        if [ "$(python3 "$VDF_TOOL" apply "$f" "$APP_ID" 2>>"$LOG" | head -1)" = done ] &&
           [ "$(python3 "$VDF_TOOL" status "$f" "$APP_ID" 2>>"$LOG" | head -1)" = ok ]; then
          ok "Option ajoutée ${D}(compte $(basename "$(dirname "$(dirname "$f")")"), copie de sauvegarde à côté)$R"
        else
          cp -f "$f.planetbase-coop-$stamp.bak" "$f"
          conflicts+=("$f")
        fi
      done
      STEAM_CLOSED_BY_US=1
    else
      manual_option_help; RESULTS+=("Option de lancement|à régler à la main"); return
    fi
  fi
  if [ ${#conflicts[@]} -gt 0 ]; then
    cur=$(python3 "$VDF_TOOL" status "${conflicts[0]}" "$APP_ID" 2>>"$LOG" | sed -n 2p)
    warn "Vos options de lancement actuelles sont trop particulières pour être modifiées sans risque."
    sub "Actuellement : $cur"
    manual_option_help
    RESULTS+=("Option de lancement|à régler à la main"); return
  fi
  RESULTS+=("Option de lancement|réglée")
}

manual_option_help() {
  helpbox "Régler l'option de lancement (une seule fois)" \
    "1. Steam : clic droit sur Planetbase > Propriétés > Général." \
    "2. Dans « Options de lancement », collez exactement :" \
    "   ${B}$OUR_OPTION$R" \
    "   (si vous avez déjà un WINEDLLOVERRIDES, ajoutez winhttp=n,b" \
    "    à sa liste, séparé par un point-virgule)" \
    "3. Fermez la fenêtre : c'est enregistré."
}

do_finish() {
  step "Terminé"
  say ""
  say "  ${GRN}╭$(rep '─' $((W - 5)))╮$R"
  local l k v
  for l in "Jeu|$(short "$GAME_DIR" 44)" "${RESULTS[@]}"; do
    k="${l%%|*}"; v="${l#*|}"
    local text="${B}$(printf '%-20s' "$k")$R $v"
    local pad=$((W - 7 - $(vlen "$text"))); [ $pad -lt 0 ] && pad=0
    say "  ${GRN}│$R $text$(rep ' ' $pad) ${GRN}│$R"
  done
  say "  ${GRN}╰$(rep '─' $((W - 5)))╯$R"
  helpbox "Pour jouer" \
    "Dans le jeu, appuyez sur ${B}F8$R : le panneau du mod s'ouvre." \
    "• Hôte : chargez une colonie, F8 > Héberger avec Steam > Inviter." \
    "• Invité : acceptez l'invitation Steam depuis le menu principal." \
    "Pour une mise à jour plus tard : relancez simplement ce script."
  say "  ${D}Journal de cette exécution : $(short "$LOG" 60)$R"
  if [ $NO_LAUNCH = 0 ] && ask "Lancer Planetbase maintenant ?" o; then
    open_url "steam://rungameid/$APP_ID"
    if [ -n "${STEAM_CLOSED_BY_US:-}" ]; then ok "Lancement demandé à Steam (il redémarre d'abord, patientez)."
    else ok "Lancement demandé à Steam."; fi
  fi
  pause_end
}

do_uninstall() {
  STEPS=3
  step "Recherche de Planetbase"
  if [ -z "$GAME_DIR" ]; then
    find_steam || die "Steam est introuvable." "Indiquez le dossier du jeu : bash $0 uninstall --game-dir <dossier>"
    find_game || die "Planetbase introuvable dans Steam." "Indiquez le dossier du jeu : bash $0 uninstall --game-dir <dossier>"
  else
    find_steam || true
  fi
  ok "Planetbase : $(short "$GAME_DIR" 60)"
  if game_running; then spin_wait "Fermez Planetbase pour continuer… (Ctrl+C pour annuler)" '! game_running' 0; fi

  step "Suppression"
  local p="$GAME_DIR/BepInEx/plugins"
  if [ -f "$p/PlanetbaseCoop.dll" ]; then rm -f "$p/PlanetbaseCoop.dll" "$p/PlanetbaseCoop.version"; ok "Mod supprimé"
  else info "Le mod n'était pas installé."; fi
  if [ -d "$GAME_DIR/BepInEx" ] && ask "Supprimer aussi BepInEx ? (non si vous utilisez d'autres mods)" n; then
    rm -rf "$GAME_DIR/BepInEx" "$GAME_DIR/winhttp.dll" "$GAME_DIR/doorstop_config.ini" "$GAME_DIR/doorstop_config.ini.bak" "$GAME_DIR/.doorstop_version"
    if [ -f "$GAME_DIR/changelog.txt" ] && grep -qi bepinex "$GAME_DIR/changelog.txt"; then rm -f "$GAME_DIR/changelog.txt"; fi
    ok "BepInEx supprimé : le jeu est revenu à l'état d'origine."
  fi

  step "Option de lancement Steam"
  local files f changed=0
  mapfile -t files < <([ -n "$STEAM_ROOT" ] && vdf_files)
  if [ ${#files[@]} -eq 0 ] || ! have python3; then
    info "Rien à faire automatiquement. Si besoin : Steam > Planetbase > Propriétés > Options de lancement."
  else
    local any=0
    for f in "${files[@]}"; do [[ "$(python3 "$VDF_TOOL" status "$f" "$APP_ID" | sed -n 2p)" == *winhttp=n,b* ]] && any=1; done
    if [ $any = 1 ] && ask "Retirer l'option de lancement ajoutée pour le mod ?" o && close_steam; then
      for f in "${files[@]}"; do
        cp -f "$f" "$f.planetbase-coop-$(date +%Y%m%d-%H%M%S).bak"
        [ "$(python3 "$VDF_TOOL" remove "$f" "$APP_ID" | head -1)" = done ] && changed=1
      done
      [ $changed = 1 ] && ok "Option de lancement retirée." || info "Option déjà absente."
    else
      info "Option de lancement laissée telle quelle."
    fi
  fi
  say ""
  ok "${B}Désinstallation terminée.$R"
  pause_end
}

# ------------------------------------------------------------------ programme
banner
logf "date: $(date -Is)  installer v$INSTALLER_VERSION  source: $SOURCE"

if [ -z "$ACTION" ]; then
  say ""
  say "  ${B}1$R  Installer ou mettre à jour   ${D}(Entrée)$R"
  say "  ${B}2$R  Désinstaller"
  say "  ${B}3$R  Quitter"
  if [ $ASSUME_YES = 1 ]; then choice=1; else printf '\n  Votre choix : '; read -r choice || choice=3; fi
  case "${choice:-1}" in 1|"") ACTION=install ;; 2) ACTION=uninstall ;; *) exit 0 ;; esac
fi

if [ "$ACTION" = uninstall ]; then do_uninstall; exit 0; fi
do_checks
do_find
do_bepinex
do_mod
do_launch_option
do_finish
