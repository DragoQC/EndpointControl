#!/usr/bin/env bash
# MeshAll: C# orchestration, Blazor Server/Tailwind UI, SQLite, native agents.
# Runs inside an existing Debian 13 machine. MIT for MeshAll's own installer.
set -Eeuo pipefail
umask 027
export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export npm_config_fetch_retries=5
export NODE_OPTIONS="${NODE_OPTIONS:-} --dns-result-order=ipv4first"
REPO="${MESHALL_REPOSITORY:-https://github.com/DragoQC/MeshAll}"
SOURCE=""; HOST="${MESHALL_HOST:-}"; PORT=5188; UPDATE=0; PULL=0; REMOVE_LEGACY=0; PROTOCOL_WORKER=1; CHECK=0
PREFIX=/opt/meshall; DATA=/var/lib/meshall; CONFIG=/etc/meshall; WORK=""
info(){ printf '\033[1;36m[MeshAll]\033[0m %s\n' "$*"; }
die(){ printf '[MeshAll] %s\n' "$*" >&2; exit 1; }
retry(){ local attempt; for attempt in 1 2 3 4; do if "$@"; then return 0; fi; sleep "$((attempt*3))"; done; return 1; }
cleanup(){ [[ -z "$WORK" ]] || rm -rf -- "$WORK"; }
trap cleanup EXIT
trap 'printf "[MeshAll] Install failed at line %s. Check the error above; configuration and persistent data are preserved.\n" "$LINENO" >&2' ERR
while (($#)); do
  case "$1" in
    --source) SOURCE="${2:?Missing source directory}"; shift 2 ;;
    --host) HOST="${2:?Missing hostname/IP}"; shift 2 ;;
    --port|--ui-port) PORT="${2:?Missing port}"; shift 2 ;;
    --update) UPDATE=1; shift ;;
    --pull) PULL=1; shift ;;
    --local) PULL=0; shift ;;
    --remove-legacy|--replace-legacy) REMOVE_LEGACY=1; shift ;;
    --without-rdp-worker) PROTOCOL_WORKER=0; shift ;;
    --check) CHECK=1; shift ;;
    --help|-h) printf '%s\n' 'MeshAll server installer (Debian 13)' '  curl -fsSL https://raw.githubusercontent.com/DragoQC/MeshAll/main/install.sh | bash' '  bash install.sh --source /path/to/MeshAll --host IP_OR_DNS' '  Options: --port 5188 --update --pull --local --remove-legacy --without-rdp-worker --check'; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
[[ $EUID -eq 0 ]] || die 'Run as root inside the Debian 13 server.'
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == debian && "${VERSION_ID:-}" == 13 ]] || die 'This installer targets Debian 13.'
[[ -d /run/systemd/system ]] || die 'A running systemd installation is required.'
[[ "$PORT" =~ ^[0-9]+$ ]] && ((PORT>1024 && PORT<65536)) || die 'Use a port between 1025 and 65535.'
case "$(dpkg --print-architecture)" in amd64) RID=linux-x64 ;; arm64) RID=linux-arm64 ;; *) die 'Supported server architectures: amd64, arm64.' ;; esac
if [[ -z "$HOST" ]]; then HOST=$(hostname -I); HOST=${HOST%% *}; fi
[[ "$HOST" =~ ^[A-Za-z0-9.:-]+$ ]] || die 'Supply --host with an IP or DNS name devices can reach.'
if [[ "$HOST" == *:* ]]; then URL_HOST="[$HOST]"; else URL_HOST="$HOST"; fi
if [[ -n "$SOURCE" ]]; then SOURCE=$(realpath "$SOURCE"); [[ -f "$SOURCE/src/MeshAll.Server/Services/NativeAgentHub.cs" ]] || die 'Source must contain the C# MeshAll orchestrator.'; fi
info "Target: $(hostname) / Debian 13 / $RID / HTTPS $PORT"
info 'Server runtime: ASP.NET Core/C#. Storage: SQLite. No MeshCentral JavaScript server.'
if ((CHECK)); then info 'Target preflight passed; no changes made.'; exit 0; fi
WORK=$(mktemp -d /tmp/meshall-install.XXXXXX)
apt-get update
apt-get install -y ca-certificates curl gnupg git jq openssl python3 rsync build-essential pkg-config unzip
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
  retry curl -fsSL https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -o "$WORK/microsoft.deb"
  dpkg -i "$WORK/microsoft.deb"; apt-get update; apt-get install -y dotnet-sdk-10.0
fi
if ! command -v node >/dev/null || ! node -e 'process.exit(Number(process.versions.node.split(".")[0])>=22?0:1)'; then
  retry curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key -o "$WORK/node.asc"
  gpg --dearmor --yes --output /usr/share/keyrings/meshall-nodesource.gpg "$WORK/node.asc"; chmod 644 /usr/share/keyrings/meshall-nodesource.gpg
  printf '%s\n' 'deb [signed-by=/usr/share/keyrings/meshall-nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main' > /etc/apt/sources.list.d/meshall-nodesource.list
  apt-get update; apt-get install -y nodejs
fi
getent passwd meshall >/dev/null || useradd --system --home-dir "$DATA" --create-home --shell /usr/sbin/nologin meshall
install -d -m 755 "$PREFIX" "$PREFIX/releases"
install -d -o meshall -g meshall -m 700 "$DATA" "$DATA/server"
install -d -o root -g meshall -m 750 "$CONFIG"
if [[ -d "$PREFIX/source" && ! -f "$PREFIX/source/src/MeshAll.Server/Services/NativeAgentHub.cs" ]]; then
  ((REMOVE_LEGACY)) || die 'An older prototype exists. Use --replace-legacy to replace it with the C# orchestrator.'
  install -d -m 700 /var/backups/meshall
  mv "$PREFIX/source" "/var/backups/meshall/prototype-source-$(date -u +%Y%m%dT%H%M%SZ)"
fi
if [[ ! -d "$PREFIX/source" ]]; then
  if [[ -n "$SOURCE" ]]; then
    install -d "$PREFIX/source"
    rsync -a --exclude=.git --exclude=node_modules --exclude=bin --exclude=obj --exclude=Data --exclude=artifacts --exclude=downloads --exclude=.env --exclude=appsettings.Local.json "$SOURCE/" "$PREFIX/source/"
  else retry git clone --depth 1 --branch main "$REPO" "$PREFIX/source"; fi
elif ((PULL)); then
  [[ -d "$PREFIX/source/.git" ]] || die 'This source was installed locally. Apply your edits and run meshall-update --local.'
  [[ -z "$(git -C "$PREFIX/source" status --porcelain)" ]] || die 'Local source edits found. Commit them or use meshall-update --local.'
  git -C "$PREFIX/source" pull --ff-only
fi
SOURCE="$PREFIX/source"
info 'Building Blazor/Tailwind assets and the C# orchestrator'
retry npm --prefix "$SOURCE/src/MeshAll.Server" ci --no-audit --no-fund
npm --prefix "$SOURCE/src/MeshAll.Server" run build
RELEASE="$PREFIX/releases/$(date -u +%Y%m%dT%H%M%SZ)-$$"; install -d "$RELEASE"
dotnet publish "$SOURCE/src/MeshAll.Server" -c Release -r "$RID" --self-contained true -o "$RELEASE" --nologo -m:1 /p:UseSharedCompilation=false
if ((PROTOCOL_WORKER)) && [[ ! -x "$PREFIX/protocol-worker/sbin/guacd" ]]; then
  info 'Building the standard RDP/SSH protocol dependency (Apache Guacamole/FreeRDP)'
  apt-get install -y autoconf automake libtool-bin libcairo2-dev libjpeg62-turbo-dev libpng-dev libossp-uuid-dev libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libpango1.0-dev libssh2-1-dev libtelnet-dev libvncserver-dev libssl-dev libwebp-dev freerdp3-dev
  BASE=https://downloads.apache.org/guacamole/1.6.0/source
  retry curl -fsSL "$BASE/guacamole-server-1.6.0.tar.gz" -o "$WORK/guacamole.tar.gz"
  retry curl -fsSL "$BASE/guacamole-server-1.6.0.tar.gz.sha256" -o "$WORK/guacamole.sha256"
  EXPECTED=$(python3 - "$WORK/guacamole.sha256" <<'PY'
import pathlib,re,sys
print(re.search(r'\b[0-9a-fA-F]{64}\b',pathlib.Path(sys.argv[1]).read_text()).group(0))
PY
)
  printf '%s  %s\n' "$EXPECTED" "$WORK/guacamole.tar.gz" | sha256sum --check --status
  tar -xzf "$WORK/guacamole.tar.gz" -C "$WORK"
  python3 - "$WORK/guacamole-server-1.6.0" "$PREFIX/protocol-worker" <<'PY'
import os,subprocess,sys
source,prefix=sys.argv[1:]
environment=dict(os.environ,CFLAGS=os.environ.get('CFLAGS','-O2')+' -Wno-error=deprecated-declarations')
subprocess.run(['./configure','--prefix='+prefix,'--disable-guaclog','--disable-werror'],cwd=source,env=environment,check=True)
subprocess.run(['make','-j2'],cwd=source,env=environment,check=True)
subprocess.run(['make','install'],cwd=source,check=True)
PY
  [[ -f "$PREFIX/protocol-worker/lib/libguac-client-rdp.so" ]] || die 'The RDP protocol library was not built.'
fi
if [[ -x "$PREFIX/protocol-worker/sbin/guacd" ]]; then
  cat > /etc/systemd/system/meshall-rdp.service <<EOF
[Unit]
Description=MeshAll standard RDP/SSH protocol worker
After=network-online.target
[Service]
User=meshall
Group=meshall
Environment=LD_LIBRARY_PATH=$PREFIX/protocol-worker/lib
ExecStart=$PREFIX/protocol-worker/sbin/guacd -f -b 127.0.0.1 -l 4822
Restart=on-failure
TasksMax=256
MemoryMax=1G
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF
fi
if [[ ! -f "$CONFIG/server.pfx" ]]; then
  PASSWORD=$(openssl rand -hex 24)
  if [[ "$HOST" =~ ^[0-9.]+$ || "$HOST" == *:* ]]; then SAN="IP:$HOST"; else SAN="DNS:$HOST"; fi
  openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 825 -keyout "$CONFIG/server.key" -out "$CONFIG/server.crt" -subj "/CN=$HOST" -addext "subjectAltName=$SAN,DNS:localhost,IP:127.0.0.1" >/dev/null 2>&1
  MESHALL_PFX_PASSWORD="$PASSWORD" openssl pkcs12 -export -out "$CONFIG/server.pfx" -inkey "$CONFIG/server.key" -in "$CONFIG/server.crt" -passout env:MESHALL_PFX_PASSWORD
  printf 'Kestrel__Certificates__Default__Path=%s\nKestrel__Certificates__Default__Password=%s\n' "$CONFIG/server.pfx" "$PASSWORD" > "$CONFIG/https.env"; unset PASSWORD
fi
FINGERPRINT=$(openssl x509 -in "$CONFIG/server.crt" -fingerprint -sha256 -noout | cut -d= -f2 | tr -d :)
PIN="sha256//$(openssl x509 -in "$CONFIG/server.crt" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | openssl base64 -A)"
jq -n --arg data "$DATA/server" --arg url "https://$URL_HOST:$PORT" --arg fingerprint "$FINGERPRINT" --arg pin "$PIN" '{MeshAll:{DataDirectory:$data,PublicUrl:$url,CertificateSha256:$fingerprint,DownloadPublicKeyPin:$pin,SiteName:"My workspace",ProtocolWorkerHost:"127.0.0.1",ProtocolWorkerPort:4822}}' > "$CONFIG/appsettings.json"
# Normalize the previous prototype's HTTPS environment to normal .NET keys.
if grep -q '^ASPNETCORE_Kestrel' "$CONFIG/https.env"; then python3 - "$CONFIG/https.env" <<'PY'
import pathlib,sys
p=pathlib.Path(sys.argv[1]);p.write_text(p.read_text().replace('ASPNETCORE_Kestrel','Kestrel'))
PY
fi
chown root:meshall "$CONFIG"/*; chmod 640 "$CONFIG"/*; chmod 600 "$CONFIG/server.key"
ln -sfn "$RELEASE" "$PREFIX/current"
cat > /etc/systemd/system/meshall.service <<EOF
[Unit]
Description=MeshAll C# orchestrator and Blazor Server UI
After=network-online.target
Wants=network-online.target
[Service]
User=meshall
Group=meshall
WorkingDirectory=$PREFIX/current
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=MESHALL_CONNECTION_FILE=$CONFIG/appsettings.json
EnvironmentFile=$CONFIG/https.env
ExecStart=$PREFIX/current/MeshAll.Server --urls https://0.0.0.0:$PORT
Restart=on-failure
RestartSec=5
UMask=0077
TasksMax=256
NoNewPrivileges=true
PrivateTmp=true
[Install]
WantedBy=multi-user.target
EOF
cat > /usr/local/bin/meshall-update <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
exec bash "$SOURCE/install.sh" --source "$SOURCE" --update --host "$HOST" --port "$PORT" "\$@"
EOF
chmod 755 /usr/local/bin/meshall-update
systemctl daemon-reload
if [[ -f /etc/systemd/system/meshall-rdp.service ]]; then systemctl enable --now meshall-rdp.service; fi
systemctl enable meshall.service; systemctl restart meshall.service
for attempt in {1..90}; do if curl -kfsS --pinnedpubkey "$PIN" "https://127.0.0.1:$PORT/health" >/dev/null 2>&1; then break; fi; sleep 1; done
curl -kfsS --pinnedpubkey "$PIN" "https://127.0.0.1:$PORT/health" >/dev/null || die 'MeshAll did not start. Check journalctl -u meshall.'
if ((REMOVE_LEGACY)); then
  info 'Retiring the old JavaScript MeshCentral server after MeshAll health verification'
  systemctl disable --now meshcentral.service 2>/dev/null || true
  systemctl disable --now meshall-engine.service 2>/dev/null || true
  rm -f /etc/systemd/system/meshcentral.service /etc/systemd/system/meshall-engine.service
  if [[ -d /opt/meshcentral/meshcentral-data ]]; then install -d -m 700 /var/backups/meshall; tar -czf "/var/backups/meshall/legacy-data-$(date -u +%Y%m%dT%H%M%SZ).tar.gz" -C /opt/meshcentral meshcentral-data; fi
  rm -rf /opt/meshcentral
  systemctl daemon-reload
fi
info 'MeshAll is running.'
printf '\nDashboard: https://%s:%s\nFirst-run setup key: %s/server/bootstrap.key\nSource: %s\nUpdate local edits: meshall-update --local\nUpdate from GitHub: meshall-update --pull\n' "$URL_HOST" "$PORT" "$DATA" "$SOURCE"
