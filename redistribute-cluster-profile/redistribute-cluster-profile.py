import ruamel.yaml
import os
import sys
import io

existing = ['gcp', 'gcp-openshift-gce-devel-ci-2']
added = ['gcp-3']

def random_choose_cluster_profile():
    import random
    return random.choice(existing + added)

def update_cluster_profile(yaml_content):
    try:
        yaml = ruamel.yaml.YAML()
        yaml.preserve_quotes = True
        data = yaml.load(yaml_content)
        modified = False
        if 'tests' in data:
            for test in data['tests']:
                if 'steps' in test and 'cluster_profile' in test['steps']:
                    if test['steps']['cluster_profile'] in existing:
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
    for root, dirs, files in os.walk(directory):
        for file in files:
            print('Processing file:', file)
            if any(file.endswith(ext) for ext in yaml_ext):
                file_path = os.path.join(root, file)
                with open(file_path, 'r', encoding='utf-8') as f:
                    content = f.read()
                updated_content = update_cluster_profile(content)
                if updated_content:
                    with open(file_path, 'w', encoding='utf-8') as f:
                        f.write(updated_content)
                else:
                    print(f"Skipped file due to error: {file_path}")

if __name__ == "__main__":
    directory = sys.argv[1] if len(sys.argv) > 1 else None
    if not directory:
        print("Please provide directory path as first argument")
    update_yaml_files(directory)
