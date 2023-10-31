import ruamel.yaml
import os
import sys
import io
import random

existing = ['gcp', 'gcp-openshift-gce-devel-ci-2']
added = ['gcp-3']

def random_choose_cluster_profile():
    return random.choice(added)

def update_cluster_profile(yaml_content, statistics={}):
    try:
        yaml = ruamel.yaml.YAML()
        yaml.preserve_quotes = True
        data = yaml.load(yaml_content)
        modified = False
        if 'tests' in data:
            for test in data['tests']:
                if 'steps' in test and 'cluster_profile' in test['steps']:
                    if test['steps']['cluster_profile'] in existing:
                        if test['steps']['cluster_profile'] not in statistics:
                            statistics[test['steps']['cluster_profile']] = 0
                        statistics[test['steps']['cluster_profile']] += 1
                        target_ratio = len(added) / (len(existing) + len(added))
                        if random.random() < target_ratio / len(existing):
                            test['steps']['cluster_profile'] = random_choose_cluster_profile()
                            modified = True
        if modified:
            stream = io.StringIO()
            yaml.dump(data, stream)
            return stream.getvalue()
        else:
            return yaml_content
    except Exception as e:
        print("Error processing YAML content:", e)
        import pdb;pdb.set_trace()
        return None

def update_yaml_files(directory):
    yaml_ext = {'.yaml', '.yml'}
    statistics = {}
    for root, dirs, files in os.walk(directory):
        for file in files:
            print('Processing file:', file)
            if any(file.endswith(ext) for ext in yaml_ext):
                file_path = os.path.join(root, file)
                with open(file_path, 'r', encoding='utf-8') as f:
                    content = f.read()
                updated_content = update_cluster_profile(content, statistics)
                if updated_content:
                    with open(file_path, 'w', encoding='utf-8') as f:
                        f.write(updated_content)
                else:
                    print(f"Skipped file due to error: {file_path}")
    print("Statistics:")
    for cluster_profile, count in statistics.items():
        print(f"{cluster_profile}: {count}")

if __name__ == "__main__":
    directory = sys.argv[1] if len(sys.argv) > 1 else None
    if not directory:
        print("Please provide directory path as first argument")
    update_yaml_files(directory)
