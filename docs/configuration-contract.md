# JWT and local authorization contract

This document describes the JWT configuration and the local authorization policy. `JwtContractOptions.Validate` checks configuration values; the host binds these options, registers JWT bearer authentication and loads the persisted PEM keys at startup, rejecting invalid key material before serving requests.

| Key | Contract |
|---|---|
| `Jwt:Issuer` | Required; local example `fcg-users` |
| `Jwt:Audience` | Required; local example `fcg-api` |
| `Jwt:Algorithm` | Only `RS256` |
| `Jwt:ExpirationMinutes` | Exactly `15` |
| `Jwt:ClockSkewSeconds` | Exactly `0` |
| `Jwt:PrivateKeyPath` | Users issuer only; PEM PKCS#8, RSA >=2048 bits, e.g. `/run/secrets/users-jwt-private.pem` |
| `Jwt:PublicKeyPath` | PEM SubjectPublicKeyInfo, RSA >=2048 bits, e.g. `/run/secrets/jwt-public.pem`; required on Users and Catalog |

Generate the pair outside Git and persist it across ordinary restarts; never generate a fresh pair each startup. Catalog receives only the public key. Notifications and Payments receive no JWT key. Key rotation/JWKS and refresh tokens are outside scope. The options contain paths only and no real credentials or keys. Deliberate key replacement may invalidate old tokens and require login. A public key cannot sign.

JWT bearer validation allows only RS256 and validate signature, issuer, audience, not-before and expiration with zero clock skew before looking up local state. Required claims preserve Phase 1: `sub` nonempty GUID, `role` exactly User or Administrator, `jti` nonempty GUID. Map inbound claims false, name claim `sub`, role claim `role`; no body/query substitution. Existing policies are `UserOrAdmin` and `AdminOnly`. `IdentityAuthorizationPolicy.Decide` receives the result of that cryptographic validation; it does not perform it.

An invalid token or required claim returns 401 before local-state availability is checked. Unavailable repository/projection returns the 503 decision with code `auth_state_unavailable` and Retry-After 1. Available Users state with genuinely absent account returns 401 (`IdentityStateSource.UsersAccount`); absent Catalog projection returns 503 (`CatalogProjection`). Active state is necessary; blocked/deleted state returns 401. AdminOnly requires Administrator in both token and current state; either lower role returns 403. Unknown policy fails closed with 403. A current higher role does not upgrade an old lower-role token. The HTTP layer maps the decision to Problem Details with traceId and Retry-After.

Authorization uses the last local snapshot read for the request. Broker failure does not erase known state, and no service reads another database. The healthy propagation target is <=5 seconds under the documented healthy local conditions, not a guarantee under faults. Previously validated requests may finish after local block/deletion. Already accepted orders continue background processing after block/deletion. Public Catalog routes remain anonymous. Policy tests do not demonstrate a measured propagation target or end-to-end authentication.
