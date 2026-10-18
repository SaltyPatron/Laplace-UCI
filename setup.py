#!/usr/bin/env python3
"""Install the configured Linux UCI service and its scoped OpenBao credential agent."""
import argparse
import grp
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request


def run(argv):
    subprocess.run(argv, check=True)


def write(path, value, mode=0o600, owner=None):
    path = Path(path)
    if path.is_symlink():
        raise ValueError('Refusing symlink: ' + str(path))
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix='.install-')
    try:
        with os.fdopen(fd, 'w') as stream:
            os.fchmod(stream.fileno(), mode)
            if owner:
                os.fchown(stream.fileno(), owner.pw_uid, owner.pw_gid)
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def foreign_environment(path):
    """Another service's private file. The UCI account must not be told to read it."""
    text = str(path)
    return Path(text).name == 'laplace-api.env' or text == '/opt/laplace/app/laplace-api.env'


def scrub_connector(connector):
    changed = False
    profiles = connector.get('profiles') if isinstance(connector, dict) else None
    if not isinstance(profiles, dict):
        return False
    for profile in profiles.values():
        files = profile.get('environmentFiles') if isinstance(profile, dict) else None
        if not isinstance(files, list):
            continue
        kept = [item for item in files if not foreign_environment(item)]
        if kept != files:
            profile['environmentFiles'] = kept
            changed = True
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--installation')
    parser.add_argument('--enrollment')
    parser.add_argument('--render')
    parser.add_argument('--refresh', help='Rewrite the installed connector and restart its service.')
    args = parser.parse_args()
    if args.refresh:
        if os.geteuid() != 0:
            parser.error('Run this refresh with sudo.')
        installed = Path(args.refresh)
        c = json.loads(installed.read_text())
        scrub_connector(c['connector'])
        configuration = Path(c['configuration_directory'])
        account = pwd.getpwnam(c['user'])
        config_path = configuration / 'connector.json'
        write(config_path, json.dumps(c['connector'], indent=2), 0o640)
        os.chown(config_path, 0, account.pw_gid)
        write(installed, json.dumps(c), 0o640)
        os.chown(installed, 0, account.pw_gid)
        run(['systemctl', 'restart', c['service'] + '.service'])
        write(configuration / 'refresh-receipt.json', json.dumps({'refreshed_at': time.time()}), 0o644)
        return
    if args.render:
        c = json.loads(Path(args.render).read_text())
        state = Path(c['state_directory'])
        record = json.loads((state / 'candidate.json').read_text())
        for field, filename in c['secret_files'].items():
            if Path(filename).name != filename or not isinstance(record.get(field), str) or not record[field]:
                raise ValueError('Missing or invalid UCI credential field: ' + field)
            write(state / 'secrets' / filename, record[field])
        write(state / 'credential-receipt.json', json.dumps({'source': 'openbao-agent', 'rendered_at': time.time()}))
        return
    if not args.installation:
        parser.error('--installation is required.')
    if os.geteuid() != 0:
        parser.error('Run this installation operation with sudo.')
    c = json.loads(Path(args.installation).read_text())
    enrollment_path = Path(args.enrollment) if args.enrollment else Path(c['configuration_directory']) / 'enrollment.json'
    enrollment = json.loads(enrollment_path.read_text())
    if c['host'].lower() != socket.gethostname().lower().split('.')[0]:
        raise ValueError('Installation belongs to another host.')
    user, service = c['user'], c['service']
    if not re.fullmatch(r'[a-z][a-z0-9-]{0,30}', user) or not re.fullmatch(r'[a-z][a-z0-9-]+', service):
        raise ValueError('Invalid service identity.')
    for key in ['install_directory', 'state_directory', 'configuration_directory', 'publish_directory']:
        if not re.fullmatch(r'/[a-zA-Z0-9_./-]+', c[key]) or '..' in Path(c[key]).parts:
            raise ValueError('Invalid installation path: ' + key)
    if not enrollment['address'].startswith('https://') or not re.fullmatch(r'kv/data/[a-zA-Z0-9_/-]+', enrollment['record']):
        raise ValueError('OpenBao enrollment requires HTTPS and one explicit KV record.')
    source = Path(c['publish_directory']).resolve()
    if not (source / 'laplace-uci-connect').is_file():
        raise ValueError('Publish the Linux connector before installation.')
    try:
        account = pwd.getpwnam(user)
    except KeyError:
        run(['useradd', '--system', '--home-dir', c['state_directory'], '--shell', '/usr/sbin/nologin', user])
        account = pwd.getpwnam(user)
    install, state, configuration = (Path(c[k]) for k in ['install_directory', 'state_directory', 'configuration_directory'])
    for directory, private in [(install, False), (state, True), (configuration, True), (state / 'secrets', True), (state / 'logs', True)]:
        if directory.is_symlink():
            raise ValueError('Configured directory must not be a symlink: ' + str(directory))
        directory.mkdir(parents=True, exist_ok=True)
        directory.chmod(0o750 if private else 0o755)
        service_owned = directory == state or directory.parent == state
        os.chown(directory, account.pw_uid if service_owned else 0, account.pw_gid)
    # A complete revision directory keeps the serving executable intact until publication finishes.
    revision = c['revision']
    if not re.fullmatch(r'[a-f0-9]{7,64}', revision):
        raise ValueError('A source revision is required.')
    release = install / revision
    if not release.exists():
        temporary = Path(tempfile.mkdtemp(prefix='.release-', dir=install))
        shutil.copytree(source, temporary, dirs_exist_ok=True)
        for item in temporary.rglob('*'):
            if item.is_file():
                item.chmod(0o755 if item.name == 'laplace-uci-connect' else 0o644)
        temporary.chmod(0o755)
        os.replace(temporary, release)
    connector = c['connector']
    scrub_connector(connector)
    config_path = configuration / 'connector.json'
    write(configuration / 'enrollment.json', json.dumps(enrollment))
    write(configuration / 'installation.json', json.dumps(c), 0o640)
    os.chown(configuration / 'installation.json', 0, account.pw_gid)
    write(configuration / 'setup.py', Path(__file__).read_text(), 0o644)
    write(config_path, json.dumps(connector, indent=2), 0o640)
    os.chown(config_path, 0, account.pw_gid)
    for filename, content in c.get('public_files', {}).items():
        if Path(filename).name != filename or not isinstance(content, str):
            raise ValueError('Public supporting files must be text inside the configuration directory.')
        write(configuration / filename, content, 0o640)
        os.chown(configuration / filename, 0, account.pw_gid)
    for name in ['role_id', 'secret_id']:
        write(configuration / name, enrollment[name], 0o640)
        os.chown(configuration / name, 0, account.pw_gid)
    j = json.dumps
    agent = 'vault { address = ' + j(enrollment['address']) + ' }\n'
    agent += 'auto_auth { method "approle" { config = {\n'
    agent += 'role_id_file_path = ' + j(str(configuration / 'role_id')) + '\n'
    agent += 'secret_id_file_path = ' + j(str(configuration / 'secret_id')) + '\nremove_secret_id_file_after_reading = false\n} } }\n'
    agent += 'template_config { static_secret_render_interval = "1m"\nexit_on_retry_failure = true }\n'
    for field, filename in c['secret_files'].items():
        if not re.fullmatch(r'[a-zA-Z_][a-zA-Z0-9_]*', field) or Path(filename).name != filename:
            raise ValueError('Secret templates must name a field and a file within the private secret directory.')
    template = '{{ with secret "' + enrollment['record'] + '" }}{{ .Data.data | toJSON }}{{ end }}'
    agent += 'template { contents = ' + j(template) + '\ndestination = ' + j(str(state / 'candidate.json')) + '\nperms = "0600"\nbackup = false\nerror_on_missing_key = true\n'
    agent += 'exec { command = ' + j(['/usr/bin/python3', str(configuration / 'setup.py'), '--render', str(configuration / 'installation.json')]) + '\ntimeout = "30s"\n}\n}\n'
    write(configuration / 'agent.hcl', agent, 0o640)
    os.chown(configuration / 'agent.hcl', 0, account.pw_gid)
    bao = shutil.which('bao')
    if not bao:
        raise ValueError('Install the OpenBao CLI before setup.')
    common = f'User={user}\nGroup={grp.getgrgid(account.pw_gid).gr_name}\nUMask=0077\nNoNewPrivileges=true\nProtectSystem=strict\nProtectHome=true\nReadWritePaths={state}\nRestart=on-failure\nRestartSec=5\n'
    units = Path('/etc/systemd/system')
    write(units / (service + '-credentials.service'), '[Unit]\nDescription=Scoped UCI OpenBao credentials\nAfter=network-online.target\nWants=network-online.target\n[Service]\n' + common + f'ExecStart={bao} agent -config={configuration}/agent.hcl\n[Install]\nWantedBy=multi-user.target\n', 0o644)
    write(units / (service + '.service'), f'[Unit]\nDescription=Configured Laplace UCI transport\nRequires={service}-credentials.service\nAfter={service}-credentials.service\n[Service]\n' + common + f'WorkingDirectory={state}\nExecStart={release}/laplace-uci-connect serve --configuration {config_path}\n[Install]\nWantedBy=multi-user.target\n', 0o644)
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'stop', service + '-credentials.service'])
    (state / 'candidate.json').unlink(missing_ok=True)
    before = time.time()
    run(['systemctl', 'enable', '--now', service + '-credentials.service'])
    for _ in range(60):
        receipt = state / 'credential-receipt.json'
        if receipt.exists() and json.loads(receipt.read_text())['rendered_at'] >= before and all((state / 'secrets' / f).is_file() for f in c['secret_files'].values()):
            break
        time.sleep(1)
    else:
        raise RuntimeError('The workload agent has not rendered every credential; inspect its journal.')
    run(['systemctl', 'enable', service + '.service'])
    run(['systemctl', 'restart', service + '.service'])
    for _ in range(20):
        try:
            with urllib.request.urlopen(connector['listen'].rstrip('/') + '/health/live', timeout=2) as response:
                if response.status == 200:
                    break
        except OSError:
            time.sleep(.5)
    else:
        raise RuntimeError('Installed UCI listener failed its health request.')
    if c.get('nginx_server_files'):
        snippet = Path('/etc/nginx/laplace-mono/uci.location.conf')
        snippet.parent.mkdir(exist_ok=True, mode=0o755)
        previous = {Path(p): Path(p).read_bytes() for p in c['nginx_server_files']}
        old_snippet = snippet.read_bytes() if snippet.exists() else None
        try:
            upstream = connector['listen']
            if not re.fullmatch(r'http://127\.0\.0\.1:[0-9]+', upstream):
                raise ValueError('nginx requires an explicit loopback UCI listener.')
            write(snippet, f'location /uci/ {{\n    proxy_pass {upstream};\n    proxy_http_version 1.1;\n    proxy_set_header Upgrade $http_upgrade;\n    proxy_set_header Connection "upgrade";\n    proxy_buffering off;\n    proxy_read_timeout 3600s;\n}}\n', 0o644)
            include = '    include /etc/nginx/laplace-mono/*.location.conf;'
            for path, original in previous.items():
                text = original.decode()
                if include.strip() not in text:
                    text, count = re.subn(r'(server_name[^;]+;)', r'\1\n' + include, text)
                    if count != 1:
                        raise ValueError('Expected one declared server in ' + str(path))
                    write(path, text, 0o644)
            run(['nginx', '-t'])
            run(['systemctl', 'reload', 'nginx'])
        except BaseException:
            for path, original in previous.items():
                path.write_bytes(original)
            if old_snippet is None:
                snippet.unlink(missing_ok=True)
            else:
                snippet.write_bytes(old_snippet)
            raise
    write(state / 'installation.json', json.dumps({'revision': revision, 'service': service, 'listener': connector['listen'], 'profile_searches': 'not tested by installation'}), 0o640)
    entrypoint = Path(c['entrypoint'])
    if not entrypoint.is_absolute() or (entrypoint.exists() and not entrypoint.is_symlink()):
        raise ValueError('CLI entrypoint must be an absolute path that does not replace an existing regular file.')
    if entrypoint.is_symlink() and install.resolve() not in entrypoint.resolve().parents:
        raise ValueError('CLI entrypoint belongs to another installation.')
    candidate = entrypoint.with_name(entrypoint.name + '.new')
    if candidate.exists() or candidate.is_symlink():
        raise ValueError('CLI publication is already staged at ' + str(candidate))
    candidate.symlink_to(release / 'laplace-uci-connect')
    os.replace(candidate, entrypoint)
    if enrollment_path.resolve() != (configuration / 'enrollment.json').resolve():
        enrollment_path.unlink()
    print('Installed ' + service + ' and ' + str(entrypoint) + '; OpenBao rendered credentials and the listener answered. Run verify for each engine profile.')


if __name__ == '__main__':
    main()
