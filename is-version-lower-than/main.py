import sys
import re
import ruamel.yaml
from packaging import version

IDX_DAY_OF_MONTH = 2
IDX_DAY_OF_WEEK = 4

def version_lower_than_or_equal(ver, target):
    ver_v = version.parse(ver)
    target_v = version.parse(target)
    return ver_v < target_v or ver_v == target_v

def process(data, filename):
    section_latest = data.get('releases', {}).get('latest', {})
    if not section_latest:
        return False
    release_ref = list(section_latest.keys())[0]

    if 'version' in section_latest[release_ref]:
        ver = section_latest[release_ref].get('version')
    elif 'name' in section_latest[release_ref]:
        ver = section_latest[release_ref].get('name')
    elif 'version_bounds' in section_latest[release_ref]:
        ver = section_latest[release_ref].get('version_bounds', {}).get('upper')

    if not ver or not version_lower_than_or_equal(ver, '4.9'):
        return False

    pending_replacements = []
    print('Found version', ver, 'lower than 4.9 in', filename)
    for test in data.get('tests', []):
        if 'interval' in test:
            print('found test', test['as'], 'with interval', test['interval'])
            interval = test['interval'].strip()
            if not interval.endswith('h'):
                print('unrecognised interval', interval)
                continue
            if int(interval[:-1]) < 24 * 7 * 2:
                print('interval', interval, 'is less than 2 weeks')
                pending_replacements.append((interval, '336h'))
                continue
        if 'cron' in test:
            print('found test', test['as'], 'with cron', test['cron'])
            cron = re.split(r'\s+', test['cron'].strip())
            if len(cron) == 1 and cron[0] == '@daily':
                pending_replacements.append((test['cron'], '@weekly'))
            elif len(cron) == 5 and cron[IDX_DAY_OF_MONTH] == '*' and cron[IDX_DAY_OF_WEEK] == '*':
                print('cron', cron, 'is less than bi-weekly')
                cron[IDX_DAY_OF_WEEK] = '0'
                pending_replacements.append((test['cron'], ' '.join(cron)))
            else:
                print('unrecognised cron', cron)

    return pending_replacements

if __name__ == '__main__':
    FILENAME = sys.argv[1]
    # print('processing', FILENAME)

    with open(FILENAME, 'r', encoding='utf-8') as fp:
        ycontent = fp.read()

    yaml = ruamel.yaml.YAML()
    pending = []
    for data in yaml.load_all(ycontent):
        ret = process(data, FILENAME)
        if ret:
            pending.extend(ret)

    # print(pending)
    if pending:
        with open(FILENAME, 'r', encoding='utf-8') as fp:
            content = fp.read()
        for item in pending:
            content = content.replace(item[0], item[1])

        with open(FILENAME, 'w', encoding='utf-8') as fp:
            fp.write(content)

    # print('done')
