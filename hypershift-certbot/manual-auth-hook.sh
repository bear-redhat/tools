#!/bin/sh

if [[ -z "${ROUTE53_ZONE_ID}" ]]; then
  >&2 echo "ERROR: Failed to determine the Route53 zone ID."
  exit 1
fi

ACME_HOSTNAME="_acme-challenge.${CERTBOT_DOMAIN}"

echo "Adding TXT record \"${CERTBOT_VALIDATION}\" for ${ACME_HOSTNAME} ..."
aws route53 change-resource-record-sets \
    --hosted-zone-id ${ROUTE53_ZONE_ID} \
    --change-batch "{\"Changes\":[{\"Action\":\"UPSERT\",\"ResourceRecordSet\":{\"Name\":\"${ACME_HOSTNAME}\",\"Type\":\"TXT\",\"TTL\":30,\"ResourceRecords\":[{\"Value\": \"\\\"${CERTBOT_VALIDATION}\\\"\"}]}}]}"
if [[ $? -ne 0 ]]; then
    >&2 echo "ERROR: Failed to add TXT record."
    exit 1
fi
sleep 15

echo "Waiting for Route53 to propagate the TXT record ..."
TXT_VALUE="$(dig -t txt "${ACME_HOSTNAME}" | sed -n "s/^${ACME_HOSTNAME}.*\"\(.*\)\"/\1/p")"

counter=0
while [[ "${TXT_VALUE}" != "${CERTBOT_VALIDATION}" ]] && [[ $counter -lt 5 ]]; do
    echo "Current TXT value '${TXT_VALUE}' does not match '${CERTBOT_VALIDATION}'. Wait 5 seconds before retry ..."
    sleep 5
    TXT_VALUE="$(dig -t txt "${ACME_HOSTNAME}" | sed -n "s/^${ACME_HOSTNAME}.*\"\(.*\)\"/\1/p")"
    counter=$((counter+1))
done

if [[ $counter -eq 5 ]]; then
    echo "Failed to match TXT value after 5 attempts."
fi
