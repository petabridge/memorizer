# Security Configuration

This document covers security-related configuration options for Memorizer.

---

## CORS (Cross-Origin Resource Sharing)

CORS is **always enabled** by default to allow MCP clients like Claude Code to connect to the server. The default configuration is permissive to ensure the server works out of the box.

### Default Configuration

By default, Memorizer uses permissive CORS settings:
- **All origins** are allowed (`*`)
- **All HTTP methods** are allowed (GET, POST, PUT, DELETE, etc.)
- **All headers** are allowed
- **Credentials** are not allowed (cookies, authorization headers)

This configuration works for most development and internal network deployments.

### Configuring CORS

You can customize CORS settings in `appsettings.json` or via environment variables:

#### appsettings.json

```json
{
  "Cors": {
    "AllowedOrigins": ["*"],
    "AllowedMethods": ["*"],
    "AllowedHeaders": ["*"],
    "AllowCredentials": false
  }
}
```

#### Environment Variables

When using environment variables (recommended for production), prefix with `MEMORIZER_`:

```bash
# Allow specific origins only
MEMORIZER_Cors__AllowedOrigins__0=https://app.example.com
MEMORIZER_Cors__AllowedOrigins__1=https://admin.example.com

# Allow specific methods
MEMORIZER_Cors__AllowedMethods__0=GET
MEMORIZER_Cors__AllowedMethods__1=POST

# Allow specific headers
MEMORIZER_Cors__AllowedHeaders__0=Content-Type
MEMORIZER_Cors__AllowedHeaders__1=Authorization

# Enable credentials (only works with specific origins, not "*")
MEMORIZER_Cors__AllowCredentials=true
```

### Production Recommendations

For production deployments, especially when exposed to the internet:

1. **Restrict origins** to specific domains that need access:
   ```json
   {
     "Cors": {
       "AllowedOrigins": [
         "https://your-app.example.com",
         "https://trusted-client.example.com"
       ]
     }
   }
   ```

2. **Limit methods** to only what's needed:
   ```json
   {
     "Cors": {
       "AllowedMethods": ["GET", "POST", "DELETE"]
     }
   }
   ```

3. **Enable credentials** only if you're using authentication:
   ```json
   {
     "Cors": {
       "AllowedOrigins": ["https://your-app.example.com"],
       "AllowCredentials": true
     }
   }
   ```

   **Note:** You cannot use `AllowedOrigins: ["*"]` with `AllowCredentials: true`. You must specify exact origins.

### CORS and MCP Clients

MCP (Model Context Protocol) clients connect using the Streamable HTTP transport. The MCP endpoint requires CORS to be properly configured to work with external clients.

If you're experiencing connection issues with Claude Code or other MCP clients:

1. Verify CORS is enabled (it should be by default)
2. Check that your `AllowedOrigins` includes the client's origin
3. For local development, using `["*"]` is recommended
4. For production, add specific origins as needed

### Common CORS Configurations

#### Development (Default)
Permissive settings for local development:
```json
{
  "Cors": {
    "AllowedOrigins": ["*"],
    "AllowedMethods": ["*"],
    "AllowedHeaders": ["*"],
    "AllowCredentials": false
  }
}
```

#### Internal Network
Restrict to specific internal applications:
```json
{
  "Cors": {
    "AllowedOrigins": [
      "http://internal-app:8080",
      "http://192.168.1.100:3000"
    ],
    "AllowedMethods": ["*"],
    "AllowedHeaders": ["*"],
    "AllowCredentials": false
  }
}
```

#### Production (Locked Down)
Restrict to specific production domains with credentials:
```json
{
  "Cors": {
    "AllowedOrigins": [
      "https://app.example.com",
      "https://admin.example.com"
    ],
    "AllowedMethods": ["GET", "POST", "PUT", "DELETE"],
    "AllowedHeaders": ["Content-Type", "Authorization"],
    "AllowCredentials": true
  }
}
```

---

## Database Security

### Connection Strings

Always use secure connection strings in production:

```bash
# Use SSL/TLS for database connections
MEMORIZER_ConnectionStrings__Storage="Host=db.example.com;Port=5432;Database=postgmem;Username=app_user;Password=<secure-password>;SSL Mode=Require;Trust Server Certificate=false"
```

### Credentials

- **Never commit** database passwords to source control
- Use environment variables or secrets management (e.g., Azure Key Vault, AWS Secrets Manager)
- Follow the principle of least privilege for database users
- Create application-specific database users with minimal required permissions

---

## Network Security

### Reverse Proxy

For production deployments, run Memorizer behind a reverse proxy (e.g., nginx, Caddy, Traefik):

- **TLS termination**: Handle HTTPS at the proxy level
- **Rate limiting**: Protect against abuse
- **Request filtering**: Block malicious requests
- **IP restrictions**: Limit access to known networks if appropriate

### Firewall

- Only expose necessary ports (typically just the web server port)
- Use network segmentation to isolate the database
- Consider using a VPN for administrative access

---

## General Best Practices

1. **Keep dependencies updated**: Regularly update NuGet packages for security patches
2. **Use HTTPS**: Always use TLS in production (configure at reverse proxy)
3. **Monitor logs**: Watch for unusual access patterns
4. **Backup data**: Regular database backups with secure storage
5. **Environment isolation**: Separate development, staging, and production environments

---

## Security Reporting

If you discover a security vulnerability, please report it by:
1. Opening a security advisory on GitHub (preferred)
2. Contacting the maintainers directly (see README.md for contact info)

**Do not** open public issues for security vulnerabilities.
