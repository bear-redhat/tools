import json
import urllib.request
import boto3


def fetch_gcp():
    cidrs_v4 = []
    cidrs_v6 = []

    with urllib.request.urlopen('https://www.gstatic.com/ipranges/cloud.json') as fp:
        data = json.loads(fp.read().decode())
        for prefix in data['prefixes']:
            if 'ipv4Prefix' in prefix:
                cidrs_v4.append(prefix['ipv4Prefix'])
            if 'ipv6Prefix' in prefix:
                cidrs_v6.append(prefix['ipv6Prefix'])

    return cidrs_v4, cidrs_v6


def fetch_aws():
    with urllib.request.urlopen('https://ip-ranges.amazonaws.com/ip-ranges.json') as fp:
        data = json.loads(fp.read().decode())
        cidrs_v4 = [prefix['ip_prefix'] for prefix in data['prefixes'] if prefix['service'] == 'AMAZON']
        cidrs_v6 = [prefix['ipv6_prefix'] for prefix in data['ipv6_prefixes'] if prefix['service'] == 'AMAZON']
        return cidrs_v4, cidrs_v6


def update_aws_firewall_rule(rg_arn, rules):
    client = boto3.client('network-firewall')
    stateless_rules = []
    for rule in rules:
        dest_cidrs = rule.get('dest_cidrs', [])
        src_cidrs = rule.get('src_cidrs', [])
        priority = rule.get('priority')
        assert 1 < priority < 65535

        resp = client.describe_rule_group(RuleGroupArn=rg_arn)
        update_token = resp['UpdateToken']

        stateless_rules.append({
            'Priority': priority,
            'RuleDefinition': {
                'Actions': ['aws:pass'],
                'MatchAttributes': {
                    'Protocols': [6],
                    'Sources': [
                        {'AddressDefinition': x} for x in src_cidrs
                    ],
                    'SourcePorts': [
                        {'FromPort': 80, 'ToPort': 80},
                        {'FromPort': 443, 'ToPort': 443}
                    ],
                    'DestinationPorts': [
                        {'FromPort': 80, 'ToPort': 80},
                        {'FromPort': 443, 'ToPort': 443}
                    ],
                    'Destinations': [
                        {'AddressDefinition': x} for x in dest_cidrs
                    ]
                }
            }
        })

    print('Updating AWS firewall rule group...', rg_arn)
    client.update_rule_group(
        RuleGroupArn=rg_arn,
        UpdateToken=update_token,
        RuleGroup={
            'RulesSource': {
                'StatelessRulesAndCustomActions': {
                    'StatelessRules': stateless_rules
                }
            }
        }
    )


if __name__ == '__main__':
    print('fetching IP CIDRs from GCP...')
    gcp_v4, gcp_v6 = fetch_gcp()
    print('fetching IP CIDRs from AWS...')
    aws_v4, aws_v6 = fetch_aws()
    print('preparing updates...')

    update_aws_firewall_rule('arn:aws:network-firewall:us-east-1:059165973077:stateless-rulegroup/allow-egress-cidr', [
    {
        'dest_cidrs': gcp_v4,
        'src_cidrs': ['0.0.0.0/0'],
        'priority': 100
    },
    {
        'dest_cidrs': gcp_v6,
        'src_cidrs': ['::/0'],
        'priority': 101
    },
    {
        'dest_cidrs': aws_v4,
        'src_cidrs': ['0.0.0.0/0'],
        'priority': 102
    },
    {
        'dest_cidrs': aws_v6,
        'src_cidrs': ['::/0'],
        'priority': 103
    }
    ])
