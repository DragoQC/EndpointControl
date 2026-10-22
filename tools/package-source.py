#!/usr/bin/env python3
"""Package the complete editable source while excluding machine state/secrets."""
import argparse
import hashlib
from pathlib import Path
import tarfile

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--output',default=str(root/'src/MeshAll.Server/wwwroot/downloads/MeshAll-source.tar.gz'))
args=parser.parse_args()
output=Path(args.output).resolve()
output.parent.mkdir(parents=True,exist_ok=True)
excluded={'.git','node_modules','bin','obj','Data','artifacts','test-results','.vs','.idea','downloads','meshcentral-data','meshcentral-files','meshcentral-backups'}
def include(info):
    relative=Path(info.name)
    if any(part in excluded for part in relative.parts):return None
    if relative.name.endswith(('.o','.log','.db','.db-wal','.db-shm','.user','.pfx')) or relative.name in {'.env','agent-state.json','appsettings.Local.json'}:return None
    if len(relative.parts)>3 and relative.parts[-2]=='MeshAgent' and relative.name.startswith(('meshagent_','DEBUG_meshagent_')):return None
    if 'upstream' not in relative.parts and relative.name.endswith(('.pem','.key')):return None
    if info.issym():
        local=root.joinpath(*relative.parts[1:])
        if not (local.parent/info.linkname).resolve().is_relative_to(root):return None
    info.uid=info.gid=0;info.uname=info.gname='root'
    return info
with tarfile.open(output,'w:gz') as archive:
    for entry in sorted(root.iterdir()):
        if entry.resolve()==output:continue
        archive.add(entry,arcname='MeshAll/'+entry.name,filter=include)
digest=hashlib.file_digest(output.open('rb'),'sha256').hexdigest()
print(f'{output}\nSHA256 {digest}\nBytes {output.stat().st_size}')
