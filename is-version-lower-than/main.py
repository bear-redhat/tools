import sys
import re
import ruamel.yaml
from packaging import version
import random

IDX_DAY_OF_MONTH = 2
IDX_DAY_OF_WEEK = 4

def version_lower_than_or_equal(ver, target):
    ver_v = version.parse(ver)
    if ver.find("priv") != -1:
        v = ver.split("-")
        ver_v = version.parse(v[0])
    target_v = version.parse(target)
    return (ver_v < target_v or ver_v == target_v)


def cron_string():
    return str(random.randint(0, 59)) + ' ' + str(random.randint(0, 23)) + ' ' + "*/14 * *"


def replace(test):
    if 'interval' in test:
        name = test['as'] if 'as' in test else test['name']
        print('found test', name, 'with interval', test['interval'])
        if name.startswith('promote-'):
            print('found promote test', name)
            return []
        interval = test['interval'].strip()
        if interval.endswith('h') and int(interval[:-1]) < 24 * 7 * 2:
            print('interval', interval, 'is less than 2 weeks')
            del test['interval']
            test["cron"] = cron_string()
            return ["yes"]
        elif interval.endswith('m') and int(interval[:-1]) < 24 * 7 * 2 * 60:
            print('interval', interval, 'is less than 2 weeks')
            del test['interval']
            test["cron"] = cron_string()
            return ["yes"]
        else:
            print('unrecognised interval', interval)
            return []
    if 'cron' in test:
        name = test['as'] if 'as' in test else test['name']
        print('found test', name, 'with cron', test['cron'])
        cron = re.split(r'\s+', test['cron'].strip())
        if len(cron) == 1 and cron[0] == '@daily':
            del test['cron']
            test["cron"] = cron_string()
            return ["yes"]
        elif len(cron) == 5 and cron[IDX_DAY_OF_MONTH] == '*' and cron[IDX_DAY_OF_WEEK] == '*':
            print('cron', cron, 'is less than bi-weekly')
            del test['cron']
            test["cron"] = cron_string()
            return ["yes"]
        elif len(cron) == 5:
            print('cron is satisfied')
        else:
            print('unrecognised cron', cron)

    return []


def process_ciops(data, filename):
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
        pending_replacements.extend(replace(test))

    return pending_replacements


def process_job(data, filename):
    for periodic in data.get('periodics', []):
        if 'ci.openshift.io/generator' in periodic.get('labels', {}):
            continue

        if periodic.get('name', '').startswith('promote-'):
            continue

        version_satisfied = False
        for ref in periodic.get('extra_refs', []):
            base_ref = ref.get('base_ref', '').split('-')
            if len(base_ref) != 2:
                print('unrecognised base_ref', base_ref)
                continue
            ver = base_ref[1]
            if ver and version_lower_than_or_equal(ver, '4.9'):
                version_satisfied = True
                break

        if 'job-release' in periodic.get('labels', {}):
            ver = periodic.get('labels', {}).get('job-release')
            if ver and version_lower_than_or_equal(ver, '4.9'):
                version_satisfied = True

        if not version_satisfied:
            return False

        pending_replacements = []
        print('Found version', ver, 'lower than 4.9 in', filename)
        pending_replacements.extend(replace(periodic))

        return pending_replacements


if __name__ == '__main__':
    FILENAME = sys.argv[1]
    # print('processing', FILENAME)

    with open(FILENAME, 'r', encoding='utf-8') as fp:
        ycontent = fp.read()

    yaml = ruamel.yaml.YAML()
    pending = []
    all_data = list(yaml.load_all(ycontent))
    file_changed = False
    for data in all_data:
        ret = process_ciops(data, FILENAME)
        ret2 = process_job(data, FILENAME)
        if ret or ret2:
            file_changed = True


    # print('pending', pending)
    # if pending:
    #     with open(FILENAME, 'r', encoding='utf-8') as fp:
    #         content = fp.read()
    #     for item in pending:
    #         content = content.replace(item[0], item[1])

    #     with open(FILENAME, 'w', encoding='utf-8') as fp:
    #         fp.write(content)

    if file_changed:
        with open(FILENAME, 'w', encoding='utf-8') as fp:
            yaml.dump_all(all_data, fp)

    # print('done')
