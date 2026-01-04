# Security Fixes Applied

This document outlines the security vulnerabilities identified and fixed in the Jellyfin Nudity Tagger plugin.

## Critical Issues Fixed

### 1. Path Traversal Vulnerability (CRITICAL)
**Location:** `Tasks/NudityTaggingTask.cs:169`

**Issue:** The IMDB ID was used directly in `Path.Combine()` without sanitization, allowing potential path traversal attacks (e.g., `../../etc/passwd`).

**Fix:**
- Added IMDB ID format validation using regex (`^tt\d{7,8}$`)
- Sanitized filename by removing path traversal characters (`..`, `/`, `\`)
- Added path resolution check to ensure cache files stay within the cache directory
- Added explicit validation that resolved paths don't escape the cache directory

**Code Changes:**
```csharp
// Validate IMDB ID format
if (!ImdbIdRegex.IsMatch(imdbId)) { ... }

// Sanitize and validate path
var resolvedPath = Path.GetFullPath(cacheFile);
if (!resolvedPath.StartsWith(resolvedCachePath, StringComparison.OrdinalIgnoreCase)) { ... }
```

### 2. IMDB ID Validation (MEDIUM)
**Location:** `Tasks/NudityTaggingTask.cs:165-166`

**Issue:** Only checked if IMDB ID starts with "tt" but didn't validate the full format, allowing invalid or malicious input.

**Fix:**
- Added regex validation: `^tt\d{7,8}$` (tt followed by 7-8 digits)
- Added null/whitespace checks
- Reject invalid IDs with proper logging

### 3. URL Injection (MEDIUM)
**Location:** `Tasks/NudityTaggingTask.cs:184`

**Issue:** URL was constructed using string interpolation without proper encoding.

**Fix:**
- Use `Uri` constructor to properly encode URLs
- Prevents URL injection attacks
- Ensures proper URL encoding

**Code Changes:**
```csharp
var baseUri = new Uri("https://www.imdb.com/title/");
var fullUri = new Uri(baseUri, sanitizedId + "/parentalguide");
```

## Medium Priority Issues Fixed

### 4. HttpClient Timeout (MEDIUM)
**Location:** `Tasks/NudityTaggingTask.cs:33`

**Issue:** No timeout configured on HttpClient, allowing resource exhaustion or DoS attacks.

**Fix:**
- Added 30-second timeout to HttpClient
- Prevents indefinite hangs and resource exhaustion

### 5. Input Validation (MEDIUM)
**Location:** Configuration values from HTML form

**Issue:** Configuration values weren't validated server-side, allowing extreme values that could cause DoS.

**Fix:**
- Added `ValidateAndSanitizeConfig()` method
- Validates and clamps:
  - `CacheDurationHours`: 1-8760 hours (1 hour to 1 year)
  - `RequestDelayMs`: 500-60000ms (0.5s to 60s)
  - `MinimumSeverityToTag`: Must be one of valid values
  - `TagPrefix`: Limited to 50 characters, trimmed

### 6. JSON Deserialization (MEDIUM)
**Location:** `Tasks/NudityTaggingTask.cs:175`

**Issue:** JSON deserialization from cache files without validation.

**Fix:**
- Improved error handling with proper logging
- Cache errors are logged but don't crash the plugin
- Invalid cache files are ignored and re-fetched

### 7. Exception Handling (LOW-MEDIUM)
**Location:** `Tasks/NudityTaggingTask.cs:179, 199`

**Issue:** Exceptions were silently swallowed, hiding potential security issues.

**Fix:**
- Added proper exception logging
- Errors are logged with context for debugging
- Plugin continues operation even if cache operations fail

## Low Priority Issues Fixed

### 8. Tag Length Limits (LOW)
**Location:** `Tasks/NudityTaggingTask.cs:304`

**Issue:** Tags had no length limits, potentially causing DoS or display issues.

**Fix:**
- Added maximum tag length of 100 characters
- Tagline limited to 500 characters
- Content warning in overview limited to 200 characters
- Tags are trimmed and filtered for empty/null values

### 9. Tag Content Sanitization (LOW)
**Location:** `Tasks/NudityTaggingTask.cs:304`

**Issue:** Tags weren't sanitized before application.

**Fix:**
- Tags are trimmed and validated
- Empty or whitespace-only tags are filtered
- Length limits prevent excessive data storage

## Security Best Practices Implemented

1. **Defense in Depth:** Multiple layers of validation (format check, sanitization, path resolution check)
2. **Input Validation:** All user inputs and external data are validated
3. **Proper Error Handling:** Errors are logged with context but don't expose sensitive information
4. **Resource Limits:** Timeouts and length limits prevent resource exhaustion
5. **Secure File Operations:** Path validation ensures files stay within intended directories
6. **URL Encoding:** Proper URI construction prevents injection attacks

## Testing Recommendations

1. Test with malicious IMDB IDs containing path traversal sequences
2. Test with extremely long configuration values
3. Test with invalid JSON in cache files
4. Test with network timeouts and failures
5. Test with very long tag names
6. Verify cache files are created only in the intended directory

## Additional Recommendations

1. Consider adding rate limiting for HTTP requests
2. Consider adding file size limits for cache files
3. Consider periodic cache cleanup for old files
4. Consider adding audit logging for configuration changes
5. Consider adding HTTPS certificate validation (if connecting to external APIs)

