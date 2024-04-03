#!/bin/sh

if [[ -z "${ROUTE53_ZONE_ID}" ]]; then
  >&2 echo "ERROR: Failed to determine the Route53 zone ID."
  exit 1
fi

ACME_HOSTNAME="_acme-challenge.${CERTBOT_DOMAIN}"

echo "Adding TXT record \"${CERTBOT_VALIDATION}\" for ${ACME_HOSTNAME} ..."
aws route53 change-resource-record-sets \
    --hosted-zone-id ${ROUTE53_ZONE_ID} \
    --change-batch "{\"Changes\":[{\"Action\":\"DELETE\",\"ResourceRecordSet\":{\"Name\":\"${ACME_HOSTNAME}\",\"Type\":\"TXT\",\"TTL\":30,\"ResourceRecords\":[{\"Value\": \"\\\"${CERTBOT_VALIDATION}\\\"\"}]}}]}"
if [[ $? -ne 0 ]]; then
    >&2 echo "ERROR: Failed to add TXT record."
    exit 1
fi
