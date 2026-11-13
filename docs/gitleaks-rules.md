# Imported gitleaks rules

<!-- Generated from src/Memoria.Core/Redaction/Gitleaks/rules.json by GitleaksRuleDataTests. Do not edit. -->

Memoria's redaction engine also runs gitleaks's default detection rules, from gitleaks v8.30.1 (commit `83d9cd68`, MIT licence). Gitleaks itself never runs. Each rule reads only what Memoria's own rules left, and replaces only its secret, as `[REDACTED:gitleaks-<id>]`. 214 rules are imported and 8 are left out.

## Left out

| Rule | Why |
|---|---|
| `curl-auth-user` | Covered by Memoria's curl-user-password. It replaces the user name with the password. |
| `freemius-secret-key` | Upstream applies it only to files whose path matches (?i)\.php$. A document has no path, so applying it everywhere would change what it means. |
| `generic-api-key` | Left out on purpose. Its only judge is entropy, which reads commit hashes, UUIDs and module checksums under a key-like name as secrets. Memoria's secret-assignment rule covers the same assignments, judged by the name. |
| `hashicorp-tf-password` | Upstream applies it only to files whose path matches (?i)\.(?:tf\|hcl)$. A document has no path, so applying it everywhere would change what it means. |
| `kubernetes-secret-yaml` | Upstream applies it only to files whose path matches (?i)\.ya?ml$. A document has no path, so applying it everywhere would change what it means. |
| `nuget-config-password` | Upstream applies it only to files whose path matches (?i)nuget\.config$. A document has no path, so applying it everywhere would change what it means. |
| `pkcs12-file` | Upstream applies it only to files whose path matches (?i)(?:^\|\/)[^\/]+\.p(?:12\|fx)$. A document has no path, so applying it everywhere would change what it means. |
| `private-key` | Covered by Memoria's private-key. It needs the END line, and Memoria's rule also takes a key cut off before it. |

## Corrected

| Where | Why |
|---|---|
| sourcegraph-access-token: `(?i)\b(\b(sgp_(?:[a-fA-F0-9]{16}\|local)_[a-fA-F0-9]{40}\|sgp_[a-fA-F0-9]{40}\|[a-fA-F0-9]{40})\b)(?:[\x60'"\s;]\|\\[nr]\|$)` | Its last branch was any 40-digit hex value, so wherever the keyword sgp appeared in a document, even inside another word, every commit hash was read as a Sourcegraph token. The branches with the sgp_ prefix stay. |
| global allowlist: `(?i)^true\|false\|null$` | Without a group the anchors bind to the first and last words only, so any secret containing "false", starting with "true" or ending in "null" was allowed. |

## Imported

| Rule | What it finds |
|---|---|
| `gitleaks-1password-secret-key` | Uncovered a possible 1Password secret key, potentially compromising access to secrets in vaults. |
| `gitleaks-1password-service-account-token` | Uncovered a possible 1Password service account token, potentially compromising access to secrets in vaults. |
| `gitleaks-adafruit-api-key` | Identified a potential Adafruit API Key, which could lead to unauthorized access to Adafruit services and sensitive data exposure. |
| `gitleaks-adobe-client-id` | Detected a pattern that resembles an Adobe OAuth Web Client ID, posing a risk of compromised Adobe integrations and data breaches. |
| `gitleaks-adobe-client-secret` | Discovered a potential Adobe Client Secret, which, if exposed, could allow unauthorized Adobe service access and data manipulation. |
| `gitleaks-age-secret-key` | Discovered a potential Age encryption tool secret key, risking data decryption and unauthorized access to sensitive information. |
| `gitleaks-airtable-api-key` | Uncovered a possible Airtable API Key, potentially compromising database access and leading to data leakage or alteration. |
| `gitleaks-airtable-personnal-access-token` | Uncovered a possible Airtable Personal AccessToken, potentially compromising database access and leading to data leakage or alteration. |
| `gitleaks-algolia-api-key` | Identified an Algolia API Key, which could result in unauthorized search operations and data exposure on Algolia-managed platforms. |
| `gitleaks-alibaba-access-key-id` | Detected an Alibaba Cloud AccessKey ID, posing a risk of unauthorized cloud resource access and potential data compromise. |
| `gitleaks-alibaba-secret-key` | Discovered a potential Alibaba Cloud Secret Key, potentially allowing unauthorized operations and data access within Alibaba Cloud. |
| `gitleaks-anthropic-admin-api-key` | Detected an Anthropic Admin API Key, risking unauthorized access to administrative functions and sensitive AI model configurations. |
| `gitleaks-anthropic-api-key` | Identified an Anthropic API Key, which may compromise AI assistant integrations and expose sensitive data to unauthorized access. |
| `gitleaks-artifactory-api-key` | Detected an Artifactory api key, posing a risk unauthorized access to the central repository. |
| `gitleaks-artifactory-reference-token` | Detected an Artifactory reference token, posing a risk of impersonation and unauthorized access to the central repository. |
| `gitleaks-asana-client-id` | Discovered a potential Asana Client ID, risking unauthorized access to Asana projects and sensitive task information. |
| `gitleaks-asana-client-secret` | Identified an Asana Client Secret, which could lead to compromised project management integrity and unauthorized access. |
| `gitleaks-atlassian-api-token` | Detected an Atlassian API token, posing a threat to project management and collaboration tool security and data confidentiality. |
| `gitleaks-authress-service-client-access-key` | Uncovered a possible Authress Service Client Access Key, which may compromise access control services and sensitive data. |
| `gitleaks-aws-access-token` | Identified a pattern that may indicate AWS credentials, risking unauthorized cloud resource access and data breaches on AWS platforms. |
| `gitleaks-aws-amazon-bedrock-api-key-long-lived` | Identified a pattern that may indicate long-lived Amazon Bedrock API keys, risking unauthorized Amazon Bedrock usage |
| `gitleaks-aws-amazon-bedrock-api-key-short-lived` | Identified a pattern that may indicate short-lived Amazon Bedrock API keys, risking unauthorized Amazon Bedrock usage |
| `gitleaks-azure-ad-client-secret` | Azure AD Client Secret |
| `gitleaks-beamer-api-token` | Detected a Beamer API token, potentially compromising content management and exposing sensitive notifications and updates. |
| `gitleaks-bitbucket-client-id` | Discovered a potential Bitbucket Client ID, risking unauthorized repository access and potential codebase exposure. |
| `gitleaks-bitbucket-client-secret` | Discovered a potential Bitbucket Client Secret, posing a risk of compromised code repositories and unauthorized access. |
| `gitleaks-bittrex-access-key` | Identified a Bittrex Access Key, which could lead to unauthorized access to cryptocurrency trading accounts and financial loss. |
| `gitleaks-bittrex-secret-key` | Detected a Bittrex Secret Key, potentially compromising cryptocurrency transactions and financial security. |
| `gitleaks-cisco-meraki-api-key` | Cisco Meraki is a cloud-managed IT solution that provides networking, security, and device management through an easy-to-use interface. |
| `gitleaks-clickhouse-cloud-api-secret-key` | Identified a pattern that may indicate clickhouse cloud API secret key, risking unauthorized clickhouse cloud api access and data breaches on ClickHouse Cloud platforms. |
| `gitleaks-clojars-api-token` | Uncovered a possible Clojars API token, risking unauthorized access to Clojure libraries and potential code manipulation. |
| `gitleaks-cloudflare-api-key` | Detected a Cloudflare API Key, potentially compromising cloud application deployments and operational security. |
| `gitleaks-cloudflare-global-api-key` | Detected a Cloudflare Global API Key, potentially compromising cloud application deployments and operational security. |
| `gitleaks-cloudflare-origin-ca-key` | Detected a Cloudflare Origin CA Key, potentially compromising cloud application deployments and operational security. |
| `gitleaks-codecov-access-token` | Found a pattern resembling a Codecov Access Token, posing a risk of unauthorized access to code coverage reports and sensitive data. |
| `gitleaks-cohere-api-token` | Identified a Cohere Token, posing a risk of unauthorized access to AI services and data manipulation. |
| `gitleaks-coinbase-access-token` | Detected a Coinbase Access Token, posing a risk of unauthorized access to cryptocurrency accounts and financial transactions. |
| `gitleaks-confluent-access-token` | Identified a Confluent Access Token, which could compromise access to streaming data platforms and sensitive data flow. |
| `gitleaks-confluent-secret-key` | Found a Confluent Secret Key, potentially risking unauthorized operations and data access within Confluent services. |
| `gitleaks-contentful-delivery-api-token` | Discovered a Contentful delivery API token, posing a risk to content management systems and data integrity. |
| `gitleaks-curl-auth-header` | Discovered a potential authorization token provided in a curl command header, which could compromise the curl accessed resource. |
| `gitleaks-databricks-api-token` | Uncovered a Databricks API token, which may compromise big data analytics platforms and sensitive data processing. |
| `gitleaks-datadog-access-token` | Detected a Datadog Access Token, potentially risking monitoring and analytics data exposure and manipulation. |
| `gitleaks-defined-networking-api-token` | Identified a Defined Networking API token, which could lead to unauthorized network operations and data breaches. |
| `gitleaks-digitalocean-access-token` | Found a DigitalOcean OAuth Access Token, risking unauthorized cloud resource access and data compromise. |
| `gitleaks-digitalocean-pat` | Discovered a DigitalOcean Personal Access Token, posing a threat to cloud infrastructure security and data privacy. |
| `gitleaks-digitalocean-refresh-token` | Uncovered a DigitalOcean OAuth Refresh Token, which could allow prolonged unauthorized access and resource manipulation. |
| `gitleaks-discord-api-token` | Detected a Discord API key, potentially compromising communication channels and user data privacy on Discord. |
| `gitleaks-discord-client-id` | Identified a Discord client ID, which may lead to unauthorized integrations and data exposure in Discord applications. |
| `gitleaks-discord-client-secret` | Discovered a potential Discord client secret, risking compromised Discord bot integrations and data leaks. |
| `gitleaks-doppler-api-token` | Discovered a Doppler API token, posing a risk to environment and secrets management security. |
| `gitleaks-droneci-access-token` | Detected a Droneci Access Token, potentially compromising continuous integration and deployment workflows. |
| `gitleaks-dropbox-api-token` | Identified a Dropbox API secret, which could lead to unauthorized file access and data breaches in Dropbox storage. |
| `gitleaks-dropbox-long-lived-api-token` | Found a Dropbox long-lived API token, risking prolonged unauthorized access to cloud storage and sensitive data. |
| `gitleaks-dropbox-short-lived-api-token` | Discovered a Dropbox short-lived API token, posing a risk of temporary but potentially harmful data access and manipulation. |
| `gitleaks-duffel-api-token` | Uncovered a Duffel API token, which may compromise travel platform integrations and sensitive customer data. |
| `gitleaks-dynatrace-api-token` | Detected a Dynatrace API token, potentially risking application performance monitoring and data exposure. |
| `gitleaks-easypost-api-token` | Identified an EasyPost API token, which could lead to unauthorized postal and shipment service access and data exposure. |
| `gitleaks-easypost-test-api-token` | Detected an EasyPost test API token, risking exposure of test environments and potentially sensitive shipment data. |
| `gitleaks-etsy-access-token` | Found an Etsy Access Token, potentially compromising Etsy shop management and customer data. |
| `gitleaks-facebook-access-token` | Discovered a Facebook Access Token, posing a risk of unauthorized access to Facebook accounts and personal data exposure. |
| `gitleaks-facebook-page-access-token` | Discovered a Facebook Page Access Token, posing a risk of unauthorized access to Facebook accounts and personal data exposure. |
| `gitleaks-facebook-secret` | Discovered a Facebook Application secret, posing a risk of unauthorized access to Facebook accounts and personal data exposure. |
| `gitleaks-fastly-api-token` | Uncovered a Fastly API key, which may compromise CDN and edge cloud services, leading to content delivery and security issues. |
| `gitleaks-finicity-api-token` | Detected a Finicity API token, potentially risking financial data access and unauthorized financial operations. |
| `gitleaks-finicity-client-secret` | Identified a Finicity Client Secret, which could lead to compromised financial service integrations and data breaches. |
| `gitleaks-finnhub-access-token` | Found a Finnhub Access Token, risking unauthorized access to financial market data and analytics. |
| `gitleaks-flickr-access-token` | Discovered a Flickr Access Token, posing a risk of unauthorized photo management and potential data leakage. |
| `gitleaks-flutterwave-encryption-key` | Uncovered a Flutterwave Encryption Key, which may compromise payment processing and sensitive financial information. |
| `gitleaks-flutterwave-public-key` | Detected a Finicity Public Key, potentially exposing public cryptographic operations and integrations. |
| `gitleaks-flutterwave-secret-key` | Identified a Flutterwave Secret Key, risking unauthorized financial transactions and data breaches. |
| `gitleaks-flyio-access-token` | Uncovered a Fly.io API key |
| `gitleaks-frameio-api-token` | Found a Frame.io API token, potentially compromising video collaboration and project management. |
| `gitleaks-freshbooks-access-token` | Discovered a Freshbooks Access Token, posing a risk to accounting software access and sensitive financial data exposure. |
| `gitleaks-gcp-api-key` | Uncovered a GCP API key, which could lead to unauthorized access to Google Cloud services and data breaches. |
| `gitleaks-github-app-token` | Identified a GitHub App Token, which may compromise GitHub application integrations and source code security. |
| `gitleaks-github-fine-grained-pat` | Found a GitHub Fine-Grained Personal Access Token, risking unauthorized repository access and code manipulation. |
| `gitleaks-github-oauth` | Discovered a GitHub OAuth Access Token, posing a risk of compromised GitHub account integrations and data leaks. |
| `gitleaks-github-pat` | Uncovered a GitHub Personal Access Token, potentially leading to unauthorized repository access and sensitive content exposure. |
| `gitleaks-github-refresh-token` | Detected a GitHub Refresh Token, which could allow prolonged unauthorized access to GitHub services. |
| `gitleaks-gitlab-cicd-job-token` | Identified a GitLab CI/CD Job Token, potential access to projects and some APIs on behalf of a user while the CI job is running. |
| `gitleaks-gitlab-deploy-token` | Identified a GitLab Deploy Token, risking access to repositories, packages and containers with write access. |
| `gitleaks-gitlab-feature-flag-client-token` | Identified a GitLab feature flag client token, risks exposing user lists and features flags used by an application. |
| `gitleaks-gitlab-feed-token` | Identified a GitLab feed token, risking exposure of user data. |
| `gitleaks-gitlab-incoming-mail-token` | Identified a GitLab incoming mail token, risking manipulation of data sent by mail. |
| `gitleaks-gitlab-kubernetes-agent-token` | Identified a GitLab Kubernetes Agent token, risking access to repos and registry of projects connected via agent. |
| `gitleaks-gitlab-oauth-app-secret` | Identified a GitLab OIDC Application Secret, risking access to apps using GitLab as authentication provider. |
| `gitleaks-gitlab-pat` | Identified a GitLab Personal Access Token, risking unauthorized access to GitLab repositories and codebase exposure. |
| `gitleaks-gitlab-pat-routable` | Identified a GitLab Personal Access Token (routable), risking unauthorized access to GitLab repositories and codebase exposure. |
| `gitleaks-gitlab-ptt` | Found a GitLab Pipeline Trigger Token, potentially compromising continuous integration workflows and project security. |
| `gitleaks-gitlab-rrt` | Discovered a GitLab Runner Registration Token, posing a risk to CI/CD pipeline integrity and unauthorized access. |
| `gitleaks-gitlab-runner-authentication-token` | Discovered a GitLab Runner Authentication Token, posing a risk to CI/CD pipeline integrity and unauthorized access. |
| `gitleaks-gitlab-runner-authentication-token-routable` | Discovered a GitLab Runner Authentication Token (Routable), posing a risk to CI/CD pipeline integrity and unauthorized access. |
| `gitleaks-gitlab-scim-token` | Discovered a GitLab SCIM Token, posing a risk to unauthorized access for a organization or instance. |
| `gitleaks-gitlab-session-cookie` | Discovered a GitLab Session Cookie, posing a risk to unauthorized access to a user account. |
| `gitleaks-gitter-access-token` | Uncovered a Gitter Access Token, which may lead to unauthorized access to chat and communication services. |
| `gitleaks-gocardless-api-token` | Detected a GoCardless API token, potentially risking unauthorized direct debit payment operations and financial data exposure. |
| `gitleaks-grafana-api-key` | Identified a Grafana API key, which could compromise monitoring dashboards and sensitive data analytics. |
| `gitleaks-grafana-cloud-api-token` | Found a Grafana cloud API token, risking unauthorized access to cloud-based monitoring services and data exposure. |
| `gitleaks-grafana-service-account-token` | Discovered a Grafana service account token, posing a risk of compromised monitoring services and data integrity. |
| `gitleaks-harness-api-key` | Identified a Harness Access Token (PAT or SAT), risking unauthorized access to a Harness account. |
| `gitleaks-hashicorp-tf-api-token` | Uncovered a HashiCorp Terraform user/org API token, which may lead to unauthorized infrastructure management and security breaches. |
| `gitleaks-heroku-api-key` | Detected a Heroku API Key, potentially compromising cloud application deployments and operational security. |
| `gitleaks-heroku-api-key-v2` | Detected a Heroku API Key, potentially compromising cloud application deployments and operational security. |
| `gitleaks-hubspot-api-key` | Found a HubSpot API Token, posing a risk to CRM data integrity and unauthorized marketing operations. |
| `gitleaks-huggingface-access-token` | Discovered a Hugging Face Access token, which could lead to unauthorized access to AI models and sensitive data. |
| `gitleaks-huggingface-organization-api-token` | Uncovered a Hugging Face Organization API token, potentially compromising AI organization accounts and associated data. |
| `gitleaks-infracost-api-token` | Detected an Infracost API Token, risking unauthorized access to cloud cost estimation tools and financial data. |
| `gitleaks-intercom-api-key` | Identified an Intercom API Token, which could compromise customer communication channels and data privacy. |
| `gitleaks-intra42-client-secret` | Found a Intra42 client secret, which could lead to unauthorized access to the 42School API and sensitive data. |
| `gitleaks-jfrog-api-key` | Found a JFrog API Key, posing a risk of unauthorized access to software artifact repositories and build pipelines. |
| `gitleaks-jfrog-identity-token` | Discovered a JFrog Identity Token, potentially compromising access to JFrog services and sensitive software artifacts. |
| `gitleaks-jwt` | Uncovered a JSON Web Token, which may lead to unauthorized access to web applications and sensitive user data. |
| `gitleaks-jwt-base64` | Detected a Base64-encoded JSON Web Token, posing a risk of exposing encoded authentication and data exchange information. |
| `gitleaks-kraken-access-token` | Identified a Kraken Access Token, potentially compromising cryptocurrency trading accounts and financial security. |
| `gitleaks-kucoin-access-token` | Found a Kucoin Access Token, risking unauthorized access to cryptocurrency exchange services and transactions. |
| `gitleaks-kucoin-secret-key` | Discovered a Kucoin Secret Key, which could lead to compromised cryptocurrency operations and financial data breaches. |
| `gitleaks-launchdarkly-access-token` | Uncovered a Launchdarkly Access Token, potentially compromising feature flag management and application functionality. |
| `gitleaks-linear-api-key` | Detected a Linear API Token, posing a risk to project management tools and sensitive task data. |
| `gitleaks-linear-client-secret` | Identified a Linear Client Secret, which may compromise secure integrations and sensitive project management data. |
| `gitleaks-linkedin-client-id` | Found a LinkedIn Client ID, risking unauthorized access to LinkedIn integrations and professional data exposure. |
| `gitleaks-linkedin-client-secret` | Discovered a LinkedIn Client secret, potentially compromising LinkedIn application integrations and user data. |
| `gitleaks-lob-api-key` | Uncovered a Lob API Key, which could lead to unauthorized access to mailing and address verification services. |
| `gitleaks-lob-pub-api-key` | Detected a Lob Publishable API Key, posing a risk of exposing mail and print service integrations. |
| `gitleaks-looker-client-id` | Found a Looker Client ID, risking unauthorized access to a Looker account and exposing sensitive data. |
| `gitleaks-looker-client-secret` | Found a Looker Client Secret, risking unauthorized access to a Looker account and exposing sensitive data. |
| `gitleaks-mailchimp-api-key` | Identified a Mailchimp API key, potentially compromising email marketing campaigns and subscriber data. |
| `gitleaks-mailgun-private-api-token` | Found a Mailgun private API token, risking unauthorized email service operations and data breaches. |
| `gitleaks-mailgun-pub-key` | Discovered a Mailgun public validation key, which could expose email verification processes and associated data. |
| `gitleaks-mailgun-signing-key` | Uncovered a Mailgun webhook signing key, potentially compromising email automation and data integrity. |
| `gitleaks-mapbox-api-token` | Detected a MapBox API token, posing a risk to geospatial services and sensitive location data exposure. |
| `gitleaks-mattermost-access-token` | Identified a Mattermost Access Token, which may compromise team communication channels and data privacy. |
| `gitleaks-maxmind-license-key` | Discovered a potential MaxMind license key. |
| `gitleaks-messagebird-api-token` | Found a MessageBird API token, risking unauthorized access to communication platforms and message data. |
| `gitleaks-messagebird-client-id` | Discovered a MessageBird client ID, potentially compromising API integrations and sensitive communication data. |
| `gitleaks-microsoft-teams-webhook` | Uncovered a Microsoft Teams Webhook, which could lead to unauthorized access to team collaboration tools and data leaks. |
| `gitleaks-netlify-access-token` | Detected a Netlify Access Token, potentially compromising web hosting services and site management. |
| `gitleaks-new-relic-browser-api-token` | Identified a New Relic ingest browser API token, risking unauthorized access to application performance data and analytics. |
| `gitleaks-new-relic-insert-key` | Discovered a New Relic insight insert key, compromising data injection into the platform. |
| `gitleaks-new-relic-user-api-id` | Found a New Relic user API ID, posing a risk to application monitoring services and data integrity. |
| `gitleaks-new-relic-user-api-key` | Discovered a New Relic user API Key, which could lead to compromised application insights and performance monitoring. |
| `gitleaks-notion-api-token` | Notion API token |
| `gitleaks-npm-access-token` | Uncovered an npm access token, potentially compromising package management and code repository access. |
| `gitleaks-nytimes-access-token` | Detected a Nytimes Access Token, risking unauthorized access to New York Times APIs and content services. |
| `gitleaks-octopus-deploy-api-key` | Discovered a potential Octopus Deploy API key, risking application deployments and operational security. |
| `gitleaks-okta-access-token` | Identified an Okta Access Token, which may compromise identity management services and user authentication data. |
| `gitleaks-openai-api-key` | Found an OpenAI API Key, posing a risk of unauthorized access to AI services and data manipulation. |
| `gitleaks-openshift-user-token` | Found an OpenShift user token, potentially compromising an OpenShift/Kubernetes cluster. |
| `gitleaks-perplexity-api-key` | Detected a Perplexity API key, which could lead to unauthorized access to Perplexity AI services and data exposure. |
| `gitleaks-plaid-api-token` | Discovered a Plaid API Token, potentially compromising financial data aggregation and banking services. |
| `gitleaks-plaid-client-id` | Uncovered a Plaid Client ID, which could lead to unauthorized financial service integrations and data breaches. |
| `gitleaks-plaid-secret-key` | Detected a Plaid Secret key, risking unauthorized access to financial accounts and sensitive transaction data. |
| `gitleaks-planetscale-api-token` | Identified a PlanetScale API token, potentially compromising database management and operations. |
| `gitleaks-planetscale-oauth-token` | Found a PlanetScale OAuth token, posing a risk to database access control and sensitive data integrity. |
| `gitleaks-planetscale-password` | Discovered a PlanetScale password, which could lead to unauthorized database operations and data breaches. |
| `gitleaks-postman-api-token` | Uncovered a Postman API token, potentially compromising API testing and development workflows. |
| `gitleaks-prefect-api-token` | Detected a Prefect API token, risking unauthorized access to workflow management and automation services. |
| `gitleaks-privateai-api-token` | Identified a PrivateAI Token, posing a risk of unauthorized access to AI services and data manipulation. |
| `gitleaks-pulumi-api-token` | Found a Pulumi API token, posing a risk to infrastructure as code services and cloud resource management. |
| `gitleaks-pypi-upload-token` | Discovered a PyPI upload token, potentially compromising Python package distribution and repository integrity. |
| `gitleaks-rapidapi-access-token` | Uncovered a RapidAPI Access Token, which could lead to unauthorized access to various APIs and data services. |
| `gitleaks-readme-api-token` | Detected a Readme API token, risking unauthorized documentation management and content exposure. |
| `gitleaks-rubygems-api-token` | Identified a Rubygem API token, potentially compromising Ruby library distribution and package management. |
| `gitleaks-scalingo-api-token` | Found a Scalingo API token, posing a risk to cloud platform services and application deployment security. |
| `gitleaks-sendbird-access-id` | Discovered a Sendbird Access ID, which could compromise chat and messaging platform integrations. |
| `gitleaks-sendbird-access-token` | Uncovered a Sendbird Access Token, potentially risking unauthorized access to communication services and user data. |
| `gitleaks-sendgrid-api-token` | Detected a SendGrid API token, posing a risk of unauthorized email service operations and data exposure. |
| `gitleaks-sendinblue-api-token` | Identified a Sendinblue API token, which may compromise email marketing services and subscriber data privacy. |
| `gitleaks-sentry-access-token` | Found a Sentry.io Access Token (old format), risking unauthorized access to error tracking services and sensitive application data. |
| `gitleaks-sentry-org-token` | Found a Sentry.io Organization Token, risking unauthorized access to error tracking services and sensitive application data. |
| `gitleaks-sentry-user-token` | Found a Sentry.io User Token, risking unauthorized access to error tracking services and sensitive application data. |
| `gitleaks-settlemint-application-access-token` | Found a Settlemint Application Access Token. |
| `gitleaks-settlemint-personal-access-token` | Found a Settlemint Personal Access Token. |
| `gitleaks-settlemint-service-access-token` | Found a Settlemint Service Access Token. |
| `gitleaks-shippo-api-token` | Discovered a Shippo API token, potentially compromising shipping services and customer order data. |
| `gitleaks-shopify-access-token` | Uncovered a Shopify access token, which could lead to unauthorized e-commerce platform access and data breaches. |
| `gitleaks-shopify-custom-access-token` | Detected a Shopify custom access token, potentially compromising custom app integrations and e-commerce data security. |
| `gitleaks-shopify-private-app-access-token` | Identified a Shopify private app access token, risking unauthorized access to private app data and store operations. |
| `gitleaks-shopify-shared-secret` | Found a Shopify shared secret, posing a risk to application authentication and e-commerce platform security. |
| `gitleaks-sidekiq-secret` | Discovered a Sidekiq Secret, which could lead to compromised background job processing and application data breaches. |
| `gitleaks-sidekiq-sensitive-url` | Uncovered a Sidekiq Sensitive URL, potentially exposing internal job queues and sensitive operation details. |
| `gitleaks-slack-app-token` | Detected a Slack App-level token, risking unauthorized access to Slack applications and workspace data. |
| `gitleaks-slack-bot-token` | Identified a Slack Bot token, which may compromise bot integrations and communication channel security. |
| `gitleaks-slack-config-access-token` | Found a Slack Configuration access token, posing a risk to workspace configuration and sensitive data access. |
| `gitleaks-slack-config-refresh-token` | Discovered a Slack Configuration refresh token, potentially allowing prolonged unauthorized access to configuration settings. |
| `gitleaks-slack-legacy-bot-token` | Uncovered a Slack Legacy bot token, which could lead to compromised legacy bot operations and data exposure. |
| `gitleaks-slack-legacy-token` | Detected a Slack Legacy token, risking unauthorized access to older Slack integrations and user data. |
| `gitleaks-slack-legacy-workspace-token` | Identified a Slack Legacy Workspace token, potentially compromising access to workspace data and legacy features. |
| `gitleaks-slack-user-token` | Found a Slack User token, posing a risk of unauthorized user impersonation and data access within Slack workspaces. |
| `gitleaks-slack-webhook-url` | Discovered a Slack Webhook, which could lead to unauthorized message posting and data leakage in Slack channels. |
| `gitleaks-snyk-api-token` | Uncovered a Snyk API token, potentially compromising software vulnerability scanning and code security. |
| `gitleaks-sonar-api-token` | Uncovered a Sonar API token, potentially compromising software vulnerability scanning and code security. |
| `gitleaks-sourcegraph-access-token` | Sourcegraph is a code search and navigation engine. |
| `gitleaks-square-access-token` | Detected a Square Access Token, risking unauthorized payment processing and financial transaction exposure. |
| `gitleaks-squarespace-access-token` | Identified a Squarespace Access Token, which may compromise website management and content control on Squarespace. |
| `gitleaks-stripe-access-token` | Found a Stripe Access Token, posing a risk to payment processing services and sensitive financial data. |
| `gitleaks-sumologic-access-id` | Discovered a SumoLogic Access ID, potentially compromising log management services and data analytics integrity. |
| `gitleaks-sumologic-access-token` | Uncovered a SumoLogic Access Token, which could lead to unauthorized access to log data and analytics insights. |
| `gitleaks-telegram-bot-api-token` | Detected a Telegram Bot API Token, risking unauthorized bot operations and message interception on Telegram. |
| `gitleaks-travisci-access-token` | Identified a Travis CI Access Token, potentially compromising continuous integration services and codebase security. |
| `gitleaks-twilio-api-key` | Found a Twilio API Key, posing a risk to communication services and sensitive customer interaction data. |
| `gitleaks-twitch-api-token` | Discovered a Twitch API token, which could compromise streaming services and account integrations. |
| `gitleaks-twitter-access-secret` | Uncovered a Twitter Access Secret, potentially risking unauthorized Twitter integrations and data breaches. |
| `gitleaks-twitter-access-token` | Detected a Twitter Access Token, posing a risk of unauthorized account operations and social media data exposure. |
| `gitleaks-twitter-api-key` | Identified a Twitter API Key, which may compromise Twitter application integrations and user data security. |
| `gitleaks-twitter-api-secret` | Found a Twitter API Secret, risking the security of Twitter app integrations and sensitive data access. |
| `gitleaks-twitter-bearer-token` | Discovered a Twitter Bearer Token, potentially compromising API access and data retrieval from Twitter. |
| `gitleaks-typeform-api-token` | Uncovered a Typeform API token, which could lead to unauthorized survey management and data collection. |
| `gitleaks-vault-batch-token` | Detected a Vault Batch Token, risking unauthorized access to secret management services and sensitive data. |
| `gitleaks-vault-service-token` | Identified a Vault Service Token, potentially compromising infrastructure security and access to sensitive credentials. |
| `gitleaks-yandex-access-token` | Found a Yandex Access Token, posing a risk to Yandex service integrations and user data privacy. |
| `gitleaks-yandex-api-key` | Discovered a Yandex API Key, which could lead to unauthorized access to Yandex services and data manipulation. |
| `gitleaks-yandex-aws-access-token` | Uncovered a Yandex AWS Access Token, potentially compromising cloud resource access and data security on Yandex Cloud. |
| `gitleaks-zendesk-secret-key` | Detected a Zendesk Secret Key, risking unauthorized access to customer support services and sensitive ticketing data. |
