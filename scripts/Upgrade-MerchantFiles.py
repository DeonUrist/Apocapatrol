"""Migrate merchant metadata without changing vehicle templates or texture files."""
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
source = pathlib.Path(__file__).resolve().parents[1]
types = ('general', 'mechanic', 'organic')

def selected(group):
    return next((t for t in group.get('merchantTypes', []) if t in types), 'general')

def common_id(value):
    prefix, separator, rest = value.partition('/')
    return rest if separator and prefix in types else value

def write(path, data):
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    temporary.replace(path)

layout_path = root / 'MerchantLayouts.json'
if layout_path.exists():
    data = json.loads(layout_path.read_text(encoding='utf-8-sig'))
    if data.get('version', 1) < 2:
        for layout in data.get('layouts', []):
            poses = {}
            for pose in sorted(layout.get('objects', []), key=lambda p: 0 if p['id'].startswith('general/') else 1):
                canonical = common_id(pose['id'])
                poses.setdefault(canonical, dict(pose, id=canonical))
            layout['objects'] = list(poses.values())
        data['version'] = 2
        write(layout_path, data)

marker = root / '.merchant-traffic-v1'
store_path = root / 'PatrolsAndLoot.json'
if not marker.exists() and store_path.exists():
    data = json.loads(store_path.read_text(encoding='utf-8-sig'))
    groups = data.get('merchants', [])
    if groups:
        defaults = json.loads((source / 'EditorDefaults.json').read_text())['merchants']
        for group in defaults:
            if any(selected(existing) == selected(group) for existing in groups):
                continue
            while any(existing['id'] == group['id'] for existing in groups):
                group['id'] += '-new'
            groups.append(group)
        write(store_path, data)
    marker.write_text('1', encoding='utf-8')

config_path = root.parents[1] / 'config' / 'com.denis.apocalypter.apocapatrol.cfg'
text = config_path.read_text(encoding='utf-8-sig')
settings = {
    'Max merchants': '3',
    'Merchant spawn chance': '100',
    'Merchant check interval (minutes)': '5',
}
import re
text = re.sub(r'^Merchant (?:minimum|maximum) interval \(minutes\)\s*=.*\n?', '', text, flags=re.M)
missing = [f'{key} = {value}' for key, value in settings.items() if not re.search(r'^' + re.escape(key) + r'\s*=', text, re.M)]
if missing:
    heading = '[Experimental]'
    additions = '\n\n## Merchant route population percentage and periodic check interval.\n' + '\n'.join(missing) + '\n'
    text = text.replace(heading, heading + additions, 1) if heading in text else text + '\n' + heading + additions
config_path.write_text(text, encoding='utf-8')
print('Merchant layouts, shop groups and Experimental settings upgraded.')
