<# : Planetbase Co-op - double-cliquez sur ce fichier pour installer, mettre a jour ou desinstaller le mod.
@echo off
setlocal
set "COOP_SELF=%~f0"
set "COOP_ARGS=%*"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:COOP_SELF,[Text.Encoding]::UTF8); & ([ScriptBlock]::Create($s))"
if errorlevel 2 pause
exit /b
#>
# ---------------------------------------------------------------------------------------------
# Planetbase Co-op : installation, mise à jour et désinstallation pour Windows.
# Ce fichier est à la fois un .bat (les lignes ci-dessus) et un script PowerShell (la suite).
#
# Ce qu'il fait : trouve Steam et Planetbase, vérifie la version du jeu, installe BepInEx
# (archive officielle, empreinte SHA-256 vérifiée), installe ou remplace le mod par la dernière
# version publiée (empreinte vérifiée), puis propose de lancer le jeu. Il n'écrit que dans le
# dossier du jeu.
#
# Options (en ligne de commande) : install | uninstall  -GameDir <dossier>  -Yes  -NoLaunch
# ---------------------------------------------------------------------------------------------

$InstallerVersion = 2
$AppId = 403190
$Source = 'https://raw.githubusercontent.com/Elwynn18/Planetbase-coop/main'
if ($env:COOP_SOURCE) { $Source = $env:COOP_SOURCE }
$Page = 'https://github.com/Elwynn18/Planetbase-coop'
$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip'
if ($env:COOP_BEPINEX_URL) { $BepInExUrl = $env:COOP_BEPINEX_URL }
$BepInExSha = '37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82'
$W = 74

$ErrorActionPreference = 'Stop'
$global:ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
try { $Host.UI.RawUI.WindowTitle = 'Planetbase Co-op' } catch {}

# ------------------------------------------------------------------ arguments
$Action = ''; $GameDir = ''; $AssumeYes = $false; $NoLaunch = $false
$argList = @()
if ($env:COOP_ARGS) { $argList = @([regex]::Matches($env:COOP_ARGS, '"[^"]*"|\S+') | ForEach-Object { $_.Value.Trim('"') }) }
for ($i = 0; $i -lt $argList.Count; $i++) {
    switch -Regex ($argList[$i]) {
        '^(install|update)$' { $Action = 'install' }
        '^uninstall$' { $Action = 'uninstall' }
        '^-+gamedir$|^-+game-dir$' { $i++; $GameDir = $argList[$i] }
        '^-+yes$|^-y$' { $AssumeYes = $true }
        '^-+nolaunch$|^-+no-launch$' { $NoLaunch = $true }
    }
}
$Interactive = $true
try { if ([Console]::IsInputRedirected) { $Interactive = $false } } catch {}
if (-not $Interactive) { $AssumeYes = $true }

$Log = Join-Path ([IO.Path]::GetTempPath()) 'planetbase-coop-installer.log'
try { Start-Transcript -Path $Log -Force | Out-Null } catch {}

# ------------------------------------------------------------------ affichage
function Rep([string]$c, [int]$n) { if ($n -le 0) { return '' } ; return $c * $n }
function W([string]$t, $c) { if ($c) { Write-Host $t -NoNewline -ForegroundColor $c } else { Write-Host $t -NoNewline } }
function NL { Write-Host '' }

function Banner {
    NL
    W ('╔' + (Rep '═' ($W - 2)) + '╗') Cyan; NL
    foreach ($l in @('', '✦  PLANETBASE CO-OP  ✦', 'Installation · mise à jour · désinstallation  (Windows)', '')) {
        W '║ ' Cyan
        if ($l -like '✦*') { W $l White } else { W $l DarkGray }
        W ((Rep ' ' ($W - 4 - $l.Length)) + ' ║') Cyan; NL
    }
    W ('╚' + (Rep '═' ($W - 2)) + '╝') Cyan; NL
}

$script:Step = 0; $script:Steps = 5
function Step([string]$title) {
    $script:Step++
    $t = "[$($script:Step)/$($script:Steps)] $title"
    NL; W ('══ ' + $t + ' ' + (Rep '═' ($W - 4 - $t.Length))) Blue; NL
}
function Ok([string]$t, [string]$v = '') { W '  √ ' Green; W $t; if ($v) { W $v Cyan }; NL }
function Warn([string]$t) { W '  ! ' Yellow; W $t; NL }
function Bad([string]$t) { W '  × ' Red; W $t; NL }
function Info([string]$t, [string]$v = '') { W '  • ' DarkGray; W $t; if ($v) { W $v Cyan }; NL }
function Sub([string]$t) { W "    $t" DarkGray; NL }

function HelpBox([string]$title, [string[]]$lines) {
    NL
    W ('  ┌─ ') Yellow; W $title White; W (' ' + (Rep '─' ($W - 8 - $title.Length)) + '┐') Yellow; NL
    foreach ($l in $lines) {
        W '  │ ' Yellow; W $l; W ((Rep ' ' ($W - 6 - $l.Length)) + '│') Yellow; NL
    }
    W ('  └' + (Rep '─' ($W - 5)) + '┘') Yellow; NL
}

function Short([string]$p, [int]$max = 52) {
    if ($p.Length -gt $max) { return $p.Substring(0, 16) + '…' + $p.Substring($p.Length - ($max - 17)) }
    return $p
}

function Ask([string]$q, [string]$def) {
    $hint = '[o/N]'; if ($def -eq 'o') { $hint = '[O/n]' }
    W '  ? ' Magenta
    if ($AssumeYes) { W "$q $hint "; W "→ $def (automatique)" DarkGray; NL; return ($def -eq 'o') }
    W "$q $hint "
    $a = Read-Host
    if (-not $a) { $a = $def }
    return ($a.ToLower().StartsWith('o') -or $a.ToLower().StartsWith('y'))
}

function PauseEnd { if (-not $AssumeYes) { NL; W '  Appuyez sur Entrée pour fermer.' DarkGray; [void](Read-Host) } }

function Die([string]$msg, [string[]]$help = @()) {
    Bad $msg
    if ($help.Count -gt 0) { HelpBox 'Que faire' $help }
    NL; W "  Journal de cette exécution : $(Short $Log 60)" DarkGray; NL
    PauseEnd
    try { Stop-Transcript | Out-Null } catch {}
    exit 1
}

function WaitWhile([scriptblock]$cond, [string]$msg) {
    $frames = '|/-\'; $i = 0
    while (& $cond) {
        if ($Interactive) { [Console]::Write("`r  " + $frames[$i % 4] + ' ' + $msg + ' ') }
        Start-Sleep -Milliseconds 500; $i++
    }
    if ($Interactive) { [Console]::Write("`r" + (' ' * ($msg.Length + 6)) + "`r") }
}

# ------------------------------------------------------------------ outils
$Tmp = Join-Path ([IO.Path]::GetTempPath()) ('planetbase-coop-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $Tmp -Force | Out-Null

function Sha([string]$f) {
    if (-not (Test-Path -LiteralPath $f)) { return '' }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLower()
}

function Fetch([string]$src, [string]$dst, [switch]$Progress) {
    if ($src -like 'file://*') { $src = $src.Substring(7) }
    if (-not ($src -match '^https?://')) { Copy-Item -LiteralPath $src -Destination $dst -Force; return }
    $req = [Net.HttpWebRequest]::Create($src)
    $req.UserAgent = 'PlanetbaseCoopInstaller'
    $resp = $req.GetResponse()
    $total = $resp.ContentLength
    $in = $resp.GetResponseStream()
    $out = [IO.File]::Create($dst)
    try {
        $buf = New-Object byte[] 65536; $done = 0; $last = -1
        while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $out.Write($buf, 0, $n); $done += $n
            if ($Progress -and $Interactive -and $total -gt 0) {
                $pct = [int](100 * $done / $total)
                if ($pct -ne $last) {
                    $last = $pct; $f = [int](30 * $done / $total)
                    [Console]::Write("`r    [" + (Rep '█' $f) + (Rep '░' (30 - $f)) + "] $pct %  " + [int]($done / 1024) + ' Ko ')
                }
            }
        }
        if ($Progress -and $Interactive -and $total -gt 0) { [Console]::Write("`r" + (' ' * 60) + "`r") }
    } finally { $out.Close(); $in.Close(); $resp.Close() }
}

function PeArch([string]$f) {
    try {
        $b = [IO.File]::ReadAllBytes($f)
        $off = [BitConverter]::ToInt32($b, 60)
        $m = [BitConverter]::ToUInt16($b, $off + 4)
        if ($m -eq 0x14c) { return 'x86' }
        if ($m -eq 0x8664) { return 'x64' }
    } catch {}
    return '?'
}

function GameRunning {
    if ($env:COOP_FAKE_GAME_RUNNING) { return ($env:COOP_FAKE_GAME_RUNNING -eq '1') }
    return [bool](Get-Process -Name 'Planetbase' -ErrorAction SilentlyContinue)
}

function IsAdmin {
    try { return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { return $false }
}

function OpenUrl([string]$u) {
    if ($env:COOP_FAKE_GAME_RUNNING) { Info "(test) ouverture de $u"; return }
    Start-Process $u
}

# ------------------------------------------------------------------ manifeste et mise à jour du script
$Man = @{}
function Read-Manifest {
    $f = Join-Path $Tmp 'latest.txt'
    try { Fetch "$Source/release/latest.txt" $f } catch { return $false }
    foreach ($line in [IO.File]::ReadAllLines($f)) {
        if ($line -match '^\s*#' -or -not ($line -match '=')) { continue }
        $k, $v = $line.Split('=', 2); $Man[$k.Trim()] = $v.Trim()
    }
    return ($Man['version'] -and $Man['dll'] -and $Man['sha256'])
}

function Self-Update {
    if ($env:COOP_NO_SELF_UPDATE -or -not $env:COOP_SELF) { return }
    $latest = 0; [void][int]::TryParse([string]$Man['installer_version'], [ref]$latest)
    if ($latest -le $InstallerVersion -or -not $Man['installer_bat'] -or -not $Man['installer_bat_sha256']) { return }
    Info "Une nouvelle version de ce script existe " "(v$latest ; celui-ci : v$InstallerVersion)"
    if ($AssumeYes) { Sub 'Mode automatique : je garde ce script. Pour le mettre à jour, relancez-le sans -Yes.'; return }
    if (-not (Ask 'Mettre ce script à jour avant de continuer ?' 'o')) { Sub "Ce script reste en v$InstallerVersion. Nouvelle version : $Page"; return }
    try {
        $f = Join-Path $Tmp 'new.bat'
        Fetch "$Source/$($Man['installer_bat'])" $f
        if ((Sha $f) -ne $Man['installer_bat_sha256']) { throw 'empreinte' }
        $new = [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8)
        $old = [IO.File]::ReadAllText($env:COOP_SELF, [Text.Encoding]::UTF8)
        # cmd.exe relit la fin de l'en-tête .bat après PowerShell : il doit rester identique
        $cut = $old.IndexOf("#>")
        if ($cut -lt 0 -or $new.Length -le $cut -or $new.Substring(0, $cut) -ne $old.Substring(0, $cut)) { throw 'en-tête' }
        [IO.File]::WriteAllBytes($env:COOP_SELF, [IO.File]::ReadAllBytes($f))
        Ok 'Script mis à jour, il redémarre.'
        $env:COOP_NO_SELF_UPDATE = '1'
        try { Stop-Transcript | Out-Null } catch {}
        & ([ScriptBlock]::Create($new))
        exit $LASTEXITCODE
    } catch {
        Warn 'Impossible de mettre le script à jour tout seul ; on continue avec celui-ci.'
    }
}

# ------------------------------------------------------------------ Steam et le jeu
function Find-Libraries {
    $roots = @()
    if ($env:COOP_STEAM_ROOT) { $roots = @($env:COOP_STEAM_ROOT) }
    else {
        foreach ($k in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
            try {
                $p = Get-ItemProperty -Path $k -ErrorAction Stop
                if ($p.SteamPath) { $roots += $p.SteamPath }
                if ($p.InstallPath) { $roots += $p.InstallPath }
            } catch {}
        }
        $roots += @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
    }
    $libs = New-Object System.Collections.ArrayList
    $add = { param($p) $p = $p.Replace('/', [IO.Path]::DirectorySeparatorChar).TrimEnd('\', '/'); if ((Test-Path -LiteralPath (Join-Path $p 'steamapps')) -and -not ($libs | Where-Object { $_ -eq $p })) { [void]$libs.Add($p) } }
    foreach ($r in $roots) {
        if (-not $r) { continue }
        & $add $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { $vdf = Join-Path $r 'steamapps/libraryfolders.vdf' }
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) { & $add ($m.Groups[1].Value.Replace('\\', '\')) }
        }
    }
    return , $libs
}

function Find-Game($libs) {
    foreach ($l in $libs) {
        $acf = Join-Path $l "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $acf)) { $acf = Join-Path $l "steamapps/appmanifest_$AppId.acf" }
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $m = [regex]::Match([IO.File]::ReadAllText($acf), '"installdir"\s+"([^"]+)"')
        if (-not $m.Success) { continue }
        $d = Join-Path (Join-Path (Join-Path $l 'steamapps') 'common') $m.Groups[1].Value
        if (Test-Path -LiteralPath $d) { return $d }
    }
    return $null
}

function Test-Writable([string]$dir) {
    try { $f = Join-Path $dir '.coop-write-test'; [IO.File]::WriteAllText($f, 'x'); Remove-Item -LiteralPath $f -Force; return $true } catch { return $false }
}

function Restart-AsAdmin {
    if (IsAdmin) { return $false }
    if (-not (Ask 'Windows bloque l''écriture dans le dossier du jeu. Je relance ce script en administrateur ?' 'o')) { return $false }
    try {
        if (-not ($env:COOP_SELF -and $env:COOP_SELF.ToLower().EndsWith('.bat'))) { return $false }
        if ($env:COOP_ARGS) { Start-Process -FilePath $env:COOP_SELF -ArgumentList $env:COOP_ARGS -Verb RunAs | Out-Null }
        else { Start-Process -FilePath $env:COOP_SELF -Verb RunAs | Out-Null }
        Info 'Une nouvelle fenêtre (administrateur) prend le relais. Vous pouvez fermer celle-ci.'
        try { Stop-Transcript | Out-Null } catch {}
        exit 0
    } catch { return $false }
}

# ------------------------------------------------------------------ étapes
$Results = New-Object System.Collections.ArrayList

function Do-Checks {
    Step 'Vérifications'
    Ok "PowerShell $($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor)"
    if (-not (Read-Manifest)) {
        Die 'Impossible de lire la dernière version du mod sur GitHub.' @('Vérifiez la connexion Internet, puis relancez le script.', "Page du projet : $Page")
    }
    $gv = '1.4.2'; if ($Man['game_version']) { $gv = $Man['game_version'] }
    Ok 'Dernière version du mod : ' $Man['version']
    Sub "jeu attendu : Planetbase $gv"
    Self-Update
}

function Do-Find {
    Step 'Recherche de Steam et de Planetbase'
    if ($GameDir) {
        if (-not (Test-Path -LiteralPath $GameDir)) { Die "Le dossier indiqué n'existe pas : $GameDir" }
        $script:GameDir = (Resolve-Path -LiteralPath $GameDir).Path
        Ok 'Dossier du jeu (indiqué) : ' (Short $script:GameDir 50)
    } else {
        $libs = Find-Libraries
        if ($libs.Count -eq 0) {
            Die 'Steam est introuvable.' @('1. Installez Steam depuis https://store.steampowered.com/about/', '2. Connectez-vous, installez Planetbase, puis relancez ce script.')
        }
        Ok 'Steam : ' (Short $libs[0] 60)
        if ($libs.Count -gt 1) { Info "$($libs.Count) bibliothèques Steam examinées" }
        $g = Find-Game $libs
        if (-not $g) {
            Bad "Planetbase n'est installé dans aucune bibliothèque Steam."
            if (Ask "J'ouvre la page d'installation de Planetbase dans Steam ?" 'o') {
                OpenUrl "steam://install/$AppId"
                HelpBox 'Ensuite' @("1. Dans la fenêtre Steam, lancez l'installation.", '2. Attendez la fin du téléchargement.', '3. Relancez ce script (double-clic).')
            }
            PauseEnd; exit 1
        }
        $script:GameDir = $g
        Ok 'Planetbase : ' (Short $g 60)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $script:GameDir 'Planetbase.exe'))) {
        Die "Planetbase.exe est introuvable dans ce dossier." @('Steam : clic droit sur Planetbase > Propriétés > Fichiers installés', "> Vérifier l'intégrité des fichiers du jeu, puis relancez ce script.")
    }
    $acs = Join-Path $script:GameDir 'Planetbase_Data\Managed\Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath $acs)) { $acs = Join-Path $script:GameDir 'Planetbase_Data/Managed/Assembly-CSharp.dll' }
    $have = Sha $acs
    $gv = '1.4.2'; if ($Man['game_version']) { $gv = $Man['game_version'] }
    if (-not $have) {
        Die 'Fichiers du jeu incomplets (Assembly-CSharp.dll introuvable).' @('Steam : clic droit sur Planetbase > Propriétés > Fichiers installés', "> Vérifier l'intégrité des fichiers du jeu, puis relancez ce script.")
    } elseif ($Man['game_sha256'] -and -not ((',' + $Man['game_sha256'] + ',').Contains(",$have,"))) {
        Warn "Votre jeu n'est pas la version $gv attendue (ou un mod a modifié ses fichiers)."
        HelpBox 'Pourquoi c''est important' @('Le mod refuse de connecter deux joueurs dont les jeux diffèrent.', "Pour remettre les fichiers d'origine : Steam > clic droit sur", "Planetbase > Propriétés > Fichiers installés > Vérifier l'intégrité.")
        if (-not (Ask 'Continuer quand même ?' 'n')) { PauseEnd; exit 1 }
    } else {
        Ok "Version du jeu : $gv " '(fichiers d''origine)'
    }
    if (GameRunning) {
        Warn 'Planetbase est en cours d''exécution.'
        WaitWhile { GameRunning } 'Fermez Planetbase pour continuer…'
        Ok 'Planetbase fermé.'
    }
    if (-not (Test-Writable $script:GameDir)) {
        if (-not (Restart-AsAdmin)) {
            Die "Le dossier du jeu n'est pas modifiable : $(Short $script:GameDir 44)" @('Clic droit sur ce fichier .bat > « Exécuter en tant qu''administrateur ».')
        }
    }
}

function Do-BepInEx {
    Step 'BepInEx (chargeur de mods)'
    $g = $script:GameDir
    $core = Join-Path $g 'BepInEx\core\BepInEx.Preloader.dll'
    if (-not (Test-Path -LiteralPath $core)) { $core = Join-Path $g 'BepInEx/core/BepInEx.Preloader.dll' }
    $proxy = Join-Path $g 'winhttp.dll'
    $need = $false
    if ((Test-Path -LiteralPath $core) -and (Test-Path -LiteralPath $proxy)) {
        if ((PeArch $proxy) -eq 'x64') { Warn 'BepInEx installé est la version 64 bits ; Planetbase a besoin de la 32 bits. Remplacement.'; $need = $true }
        else { Ok 'BepInEx déjà installé ' '(32 bits)' }
    } else { Info "BepInEx n'est pas installé."; $need = $true }
    if ($need) {
        W '  ↓ ' Cyan; W 'Téléchargement de BepInEx 5.4.23.5 (32 bits, archive officielle)…'; NL
        $zip = Join-Path $Tmp 'bepinex.zip'
        try { Fetch $BepInExUrl $zip -Progress } catch { Die 'Téléchargement de BepInEx impossible.' @('Vérifiez la connexion Internet, puis relancez le script.') }
        if ((Sha $zip) -ne $BepInExSha) { Die "L'archive BepInEx reçue n'a pas la bonne empreinte : rien n'a été installé." @("Relancez le script ; si cela se reproduit, signalez-le sur $Page") }
        Ok 'Archive vérifiée ' "(SHA-256 $($BepInExSha.Substring(0, 12))…)"
        try { Expand-Archive -LiteralPath $zip -DestinationPath $g -Force } catch { Die "Décompression de BepInEx impossible : $($_.Exception.Message)" }
        Ok 'BepInEx installé'
    }
    New-Item -ItemType Directory -Path (Join-Path (Join-Path $g 'BepInEx') 'plugins') -Force | Out-Null
    $ini = Join-Path $g 'doorstop_config.ini'
    if (Test-Path -LiteralPath $ini) {
        $t = [IO.File]::ReadAllText($ini)
        if ($t -match '(?m)^\s*enabled\s*=\s*false') {
            Copy-Item -LiteralPath $ini -Destination "$ini.bak" -Force
            [IO.File]::WriteAllText($ini, ($t -replace '(?m)^(\s*enabled\s*=\s*)false', '${1}true'))
            Ok 'BepInEx était désactivé (doorstop_config.ini) : réactivé ' '(copie : doorstop_config.ini.bak)'
        }
    }
    [void]$Results.Add(@('BepInEx', 'prêt (32 bits)'))
}

function Do-Mod {
    Step 'Le mod Planetbase Co-op'
    $plugins = Join-Path (Join-Path $script:GameDir 'BepInEx') 'plugins'
    $dst = Join-Path $plugins 'PlanetbaseCoop.dll'
    $verfile = Join-Path $plugins 'PlanetbaseCoop.version'
    $new = $Man['version']
    $oldsha = Sha $dst
    $old = ''; if (Test-Path -LiteralPath $verfile) { $old = ([IO.File]::ReadAllText($verfile)).Trim() }
    if ($oldsha -eq $Man['sha256']) {
        Ok 'Déjà à jour : version ' $new
        [IO.File]::WriteAllText($verfile, "$new`n")
        [void]$Results.Add(@('Mod', "version $new (déjà à jour)")); return
    }
    $oldName = 'ancienne version'; if ($old) { $oldName = $old }
    if ($oldsha) { Info "Version installée : $oldName → mise à jour vers " $new } else { Info 'Première installation du mod, version ' $new }
    W '  ↓ ' Cyan; W 'Téléchargement du mod…'; NL
    $tmpDll = Join-Path $Tmp 'PlanetbaseCoop.dll'
    try { Fetch "$Source/$($Man['dll'])" $tmpDll -Progress } catch { Die 'Téléchargement du mod impossible.' @('Vérifiez la connexion Internet, puis relancez le script.') }
    if ((Sha $tmpDll) -ne $Man['sha256']) {
        Die "Le fichier du mod reçu n'a pas la bonne empreinte : rien n'a été remplacé." @('GitHub vient peut-être d''être mis à jour : réessayez dans 5 minutes.', "Si cela se reproduit, signalez-le sur $Page")
    }
    Ok 'Fichier vérifié ' "(SHA-256 $($Man['sha256'].Substring(0, 12))…)"
    if ($oldsha) {
        $bk = Join-Path (Join-Path $script:GameDir 'BepInEx') 'coop-backup'
        New-Item -ItemType Directory -Path $bk -Force | Out-Null
        $bkName = 'ancienne'; if ($old) { $bkName = $old }
        Copy-Item -LiteralPath $dst -Destination (Join-Path $bk "PlanetbaseCoop-$bkName.dll") -Force
        Sub 'ancienne version gardée dans BepInEx\coop-backup\'
    }
    try { Copy-Item -LiteralPath $tmpDll -Destination $dst -Force } catch { Die "Impossible d'écrire $dst" }
    [IO.File]::WriteAllText($verfile, "$new`n")
    if ($oldsha) { Ok "Mod mis à jour : $oldName → " $new; [void]$Results.Add(@('Mod', "mis à jour : $oldName → $new")) }
    else { Ok 'Mod installé : version ' $new; [void]$Results.Add(@('Mod', "installé : version $new")) }
}

function Do-Finish {
    Step 'Terminé'
    NL
    W ('  ┌' + (Rep '─' ($W - 5)) + '┐') Green; NL
    $rows = @(, @('Jeu', (Short $script:GameDir 44))) + $Results
    foreach ($r in $rows) {
        $k = $r[0].PadRight(20); $v = $r[1]
        W '  │ ' Green; W $k White; W " $v"; W ((Rep ' ' ($W - 8 - $k.Length - $v.Length)) + ' │') Green; NL
    }
    W ('  └' + (Rep '─' ($W - 5)) + '┘') Green; NL
    HelpBox 'Pour jouer' @("Dans le jeu, appuyez sur F8 : le panneau du mod s'ouvre.", '• Hôte : chargez une colonie, F8 > Héberger avec Steam > Inviter.', "• Invité : acceptez l'invitation Steam depuis le menu principal.", 'Pour une mise à jour plus tard : relancez simplement ce fichier.')
    W "  Journal de cette exécution : $(Short $Log 60)" DarkGray; NL
    if (-not $NoLaunch -and (Ask 'Lancer Planetbase maintenant ?' 'o')) {
        OpenUrl "steam://rungameid/$AppId"
        Ok 'Lancement demandé à Steam.'
    }
    PauseEnd
}

function Do-Uninstall {
    $script:Steps = 2
    Step 'Recherche de Planetbase'
    if (-not $GameDir) {
        $libs = Find-Libraries
        $g = Find-Game $libs
        if (-not $g) { Die 'Planetbase introuvable dans Steam.' @('Indiquez le dossier du jeu en ligne de commande :', 'Planetbase-Coop.bat uninstall -GameDir "<dossier du jeu>"') }
        $script:GameDir = $g
    }
    Ok 'Planetbase : ' (Short $script:GameDir 60)
    if (GameRunning) { WaitWhile { GameRunning } 'Fermez Planetbase pour continuer…' }
    if (-not (Test-Writable $script:GameDir)) { if (-not (Restart-AsAdmin)) { Die 'Le dossier du jeu n''est pas modifiable.' @('Clic droit sur ce fichier .bat > « Exécuter en tant qu''administrateur ».') } }

    Step 'Suppression'
    $g = $script:GameDir
    $p = Join-Path (Join-Path $g 'BepInEx') 'plugins'
    if (Test-Path -LiteralPath (Join-Path $p 'PlanetbaseCoop.dll')) {
        Remove-Item -LiteralPath (Join-Path $p 'PlanetbaseCoop.dll') -Force
        if (Test-Path -LiteralPath (Join-Path $p 'PlanetbaseCoop.version')) { Remove-Item -LiteralPath (Join-Path $p 'PlanetbaseCoop.version') -Force }
        Ok 'Mod supprimé'
    } else { Info "Le mod n'était pas installé." }
    if ((Test-Path -LiteralPath (Join-Path $g 'BepInEx')) -and (Ask 'Supprimer aussi BepInEx ? (non si vous utilisez d''autres mods)' 'n')) {
        foreach ($f in @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', 'doorstop_config.ini.bak', '.doorstop_version')) {
            $x = Join-Path $g $f; if (Test-Path -LiteralPath $x) { Remove-Item -LiteralPath $x -Recurse -Force }
        }
        $cl = Join-Path $g 'changelog.txt'
        if ((Test-Path -LiteralPath $cl) -and ([IO.File]::ReadAllText($cl) -match 'BepInEx')) { Remove-Item -LiteralPath $cl -Force }
        Ok "BepInEx supprimé : le jeu est revenu à l'état d'origine."
    }
    NL; Ok 'Désinstallation terminée.'
    PauseEnd
}

# ------------------------------------------------------------------ programme
try {
    Banner
    if (-not $Action) {
        NL
        W '  1' White; W '  Installer ou mettre à jour   '; W '(Entrée)' DarkGray; NL
        W '  2' White; W '  Désinstaller'; NL
        W '  3' White; W '  Quitter'; NL
        $c = '1'
        if (-not $AssumeYes) { NL; W '  Votre choix : '; $c = Read-Host; if (-not $c) { $c = '1' } }
        switch ($c.Trim()) { '1' { $Action = 'install' } '2' { $Action = 'uninstall' } default { exit 0 } }
    }
    if ($Action -eq 'uninstall') { Do-Uninstall }
    else {
        Do-Checks
        Do-Find
        Do-BepInEx
        Do-Mod
        Do-Finish
    }
    try { Stop-Transcript | Out-Null } catch {}
    exit 0
} catch {
    NL
    Bad "Erreur inattendue : $($_.Exception.Message)"
    Sub "à la ligne $($_.InvocationInfo.ScriptLineNumber)"
    HelpBox 'Que faire' @('Relancez le script. Si cela se reproduit, envoyez le journal', "$(Short $Log 60)", "à l'hôte de la partie ou sur $Page")
    PauseEnd
    try { Stop-Transcript | Out-Null } catch {}
    exit 1
} finally {
    try { Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}
