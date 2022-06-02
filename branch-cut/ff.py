import ruamel.yaml
import sys


NON_OCP_LABELS = ['px-approved', 'qe-approved', 'docs-approved']


def is_ocp(yaml):
    current_node = yaml
    if 'tide' not in current_node:
        raise ValueError('tide not found')
    current_node = current_node['tide']
    if 'queries' not in current_node:
        raise ValueError('queries not found')
    current_node = current_node['queries']

    # its not an OCP repo when `-required` labels present
    for query in current_node:
        if 'labels' not in query:
            print('labels not found')
            continue
        labels = query['labels']
        x = [x for x in labels if x in NON_OCP_LABELS]
        if len(x) > 0:
            return False

    # its is an OCP repo when `cherry-pick-approved` label presents
    for query in current_node:
        if 'labels' not in query:
            print('labels not found')
            continue
        labels = query['labels']
        if 'cherry-pick-approved' in labels:
            return True


def write_master(yaml):
    current_node = yaml
    if 'tide' not in current_node:
        raise ValueError('tide not found')
    current_node = current_node['tide']
    if 'queries' not in current_node:
        raise ValueError('queries not found')
    current_node = current_node['queries']
    for query in current_node:
        if 'includedBranches' not in query:
            print('includedBranches not found')
            continue
        if 'labels' not in query:
            print('labels not found')
            continue
        branches = query['includedBranches']
        if not 'master' in branches or not 'main' in branches:
            print('master or main not found')
            continue
        labels = query['labels']
        if not 'lgtm' in labels or not 'approved' in labels:
            print('lgtm or approved not found')
            continue
        labels.append('bugzilla/valid-bug')


if __name__ == '__main__':
    yaml = ruamel.yaml.YAML()
    FILENAME = sys.argv[1]
    with open(FILENAME, 'r', encoding='utf-8') as fp:
        data = yaml.load(fp)
    if not is_ocp(data):
        print('this is no-feature-freeze branch')
        exit(0)

    # write to master branch section
    write_master(data)
    print(f'writing file: {FILENAME}')
    with open(FILENAME, 'w', encoding='utf-8') as fp:
        yaml.dump(data, fp)
    print('done')
