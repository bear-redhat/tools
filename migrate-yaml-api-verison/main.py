import ruamel.yaml
import sys

KIND_APIVERSION = {
    'BuildConfig': 'build.openshift.io/v1',
    'Route': 'route.openshift.io/v1',
    'ImageStream': 'image.openshift.io/v1',
}

def migrate_to_full_name(node):
    if isinstance(node, ruamel.yaml.comments.CommentedSeq):
        for i in range(len(node)):
            node[i] = migrate_to_full_name(node[i])

    if isinstance(node, ruamel.yaml.comments.CommentedMap):
        if 'apiVersion' in node and node['apiVersion'] == 'v1':
            if KIND_APIVERSION.get(node.get('kind')) is not None:
                print(FILENAME, node.get('kind'), node['apiVersion'])

    return node

if __name__ == '__main__':
    FILENAME = sys.argv[1]
    # print('processing', FILENAME)

    with open(FILENAME, 'r', encoding='utf-8') as fp:
        ycontent = fp.read()

    yaml = ruamel.yaml.YAML()
    for data in yaml.load_all(ycontent):
        data = migrate_to_full_name(data)

    # with open(FILENAME, 'w', encoding='utf-8') as fp:
    #     with ruamel.yaml.YAML(output=fp, typ='rt') as yaml:
    #         # yaml.explicit_start = True
    #         # yaml.default_flow_style = True
    #         for data in yaml.load_all(ycontent):
    #             data = migrate_to_full_name(data)
    #             print('writing part', FILENAME)
    #             yaml.dump(data)
