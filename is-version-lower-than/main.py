import sys
import ruamel.yaml
from packaging import version


def version_lower_than_or_equal(ver, target):
    ver_v = version.parse(ver)
    target_v = version.parse(target)
    return ver_v < target_v or ver_v == target_v

def process(data, filename):
    ver = data.get('releases', {}).get('latest', {}).get('candidate', {}).get('version')
    if not ver or not version_lower_than_or_equal(ver, '4.9'):
        return False

    print('Found version', ver, 'lower than 4.9 in', filename)
    for test in data.get('tests', []):
        if 'interval' in test:
            print('found test', test['as'], 'with interval', test['interval'])
        if 'cron' in test:
            print('found test', test['as'], 'with cron', test['cron'])


if __name__ == '__main__':
    FILENAME = sys.argv[1]
    # print('processing', FILENAME)

    with open(FILENAME, 'r', encoding='utf-8') as fp:
        ycontent = fp.read()

    yaml = ruamel.yaml.YAML()
    for data in yaml.load_all(ycontent):
        process(data, FILENAME)
