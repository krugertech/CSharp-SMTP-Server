# Microsoft 365 tenant authentication for journal SMTP

## Objective

Accept journal reports from one customer using Exchange Online while rejecting unrelated SMTP traffic and other Microsoft 365 tenants.

## Microsoft 365 network endpoint feed

Microsoft provides an official REST service containing Microsoft 365 URLs, CIDR ranges, ports, service areas, categories, and change versions.

```text
GET https://endpoints.office.com/endpoints/Worldwide?ClientRequestId=<guid>
GET https://endpoints.office.com/endpoints/Worldwide?ServiceAreas=Exchange&ClientRequestId=<guid>
GET https://endpoints.office.com/version/Worldwide?ClientRequestId=<guid>
GET https://endpoints.office.com/changes/Worldwide/<current-version>?ClientRequestId=<guid>
```

Generate and retain a unique client GUID. Check `version` periodically and refresh only when it changes. For SMTP delivery, Exchange endpoint set **ID 10** contains Exchange Online Protection CIDRs and domains for TCP 25.

This is not every Microsoft-owned IP address. Use it to permit Exchange Online connectivity, not to identify a customer: Microsoft 365 IP ranges are shared by many tenants.

## Recommended direct-delivery controls

Use all of these controls together:

1. Assign each customer a high-entropy recipient, preferably under a dedicated customer subdomain, for example `jrn-<random-256-bit-token>@customer-id.journal.example.com`.
2. Reject unknown recipients during SMTP `RCPT TO`; support token revocation and rotation.
3. Accept TCP 25 only from endpoint-set-10 CIDRs and update them automatically.
4. Require STARTTLS and present a publicly trusted certificate for `smtp.journal.example.com`.
5. Strictly validate the Exchange journal-report wrapper, attachment structure, MIME, and size limits.

The address token is a bearer secret. It should not be treated as cryptographic proof if it leaks.

## Exchange Online forced-TLS connector

This can be configured entirely in Microsoft 365/Exchange Online; no on-premises Exchange Server is required.

```powershell
New-OutboundConnector `
  -Name "Live Journal Delivery" `
  -ConnectorType Partner `
  -RecipientDomains "customer-id.journal.example.com" `
  -SmartHosts "smtp.journal.example.com" `
  -UseMxRecord $false `
  -TlsSettings DomainValidation `
  -TlsDomain "smtp.journal.example.com" `
  -IsTransportRuleScoped $false
```

`DomainValidation` makes Exchange Online validate the journal server's certificate and prevents plaintext fallback. Keep the connector recipient-domain scoped: system-generated journal reports are not processed by normal mail-flow rules.

## Tenant-authentication limitation

Exchange Online presents Microsoft's shared TLS identity (`mail.protection.outlook.com`) and uses shared outbound IP ranges. Checking that certificate and the endpoint feed proves that the connection came from Microsoft 365, but not which tenant sent it.

Do not use the visible sender, envelope sender, `EHLO`, Microsoft `X-` headers, or a transport-rule-added secret header as tenant authentication. DKIM may be an additional signal only after tests confirm that the outer journal reports are consistently signed by the customer's domain.

## Strong tenant-specific authentication

For cryptographic proof of the customer, insert a customer-specific relay:

```text
Customer Exchange Online
    -> forced TLS -> customer-controlled/dedicated relay
    -> per-customer mTLS client certificate -> journal SMTP service
```

The journal service maps the validated client certificate to the customer. A dedicated static relay IP or per-customer SMTP AUTH credential is a weaker alternative. Exchange Online itself does not present a tenant-specific client certificate to an external SMTP server.

## References

- [Microsoft 365 IP Address and URL web service](https://learn.microsoft.com/en-us/microsoft-365/enterprise/microsoft-365-ip-web-service?view=o365-worldwide)
- [Microsoft 365 URLs and IP ranges](https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges?view=o365-worldwide)
- [Exchange Online TLS behavior](https://learn.microsoft.com/en-us/purview/exchange-online-uses-tls-to-secure-email-connections)
- [Outbound connector options](https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/new-outboundconnector?view=exchange-ps)
- [Exchange Online journaling](https://learn.microsoft.com/en-us/exchange/security-and-compliance/journaling/journaling)
- [Mail-flow rule limitations](https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules)
