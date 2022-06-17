import sys
import os
from typing import List, Tuple, TypedDict
import ipwhois
import csv


class ResponseType(TypedDict):
    asn: str
    asn_cidr: str
    asn_country_code: str
    asn_description: str


def batch_whois(hosts: List[str]) -> Tuple[List[ResponseType], List[str]]:
    """
    Batch whois lookup for a list of hosts.
    """
    rs = []
    failures = []
    print('total', len(hosts))
    for idx, host in enumerate(hosts):
        print('processing', idx, host)
        if not host:
            continue
        try:
            obj = ipwhois.IPWhois(host)
        except ipwhois.exceptions.IPDefinedError as e:
            print(e)
            failures.append(host)
            continue

        try:
            resp = obj.lookup_whois()
        except ipwhois.exceptions.WhoisLookupError as e:
            print(e)
            failures.append(host)
            continue

        r: ResponseType = {}
        r["asn"] = resp["asn"]
        r["asn_cidr"] = resp["asn_cidr"]
        r["asn_country_code"] = resp["asn_country_code"]
        r["asn_description"] = resp["asn_description"]
        print(r)
        rs.append(r)
    return rs, failures

if __name__ == '__main__':
    filename = sys.argv[1]
    output = sys.argv[2]

    assert os.path.isfile(filename)

    with open(filename, 'r') as f:
        hosts = [line.strip() for line in f]

    resp, failures = batch_whois(hosts)
    with open(output, 'w') as f:
        writer = csv.DictWriter(f, fieldnames=resp[0].keys())
        writer.writeheader()
        writer.writerows(resp)
    print('the following hosts failed:', failures)
    print('done')