# SurSolver for .NET — captcha solver API client for C#

Solve **reCAPTCHA v3**, **Cloudflare Turnstile**, **Cloudflare Challenge**, **GeeTest**,
**DataDome**, **AWS WAF**, **Akamai**, **Kasada** and image captchas from C# and .NET with the
official [SurSolver](https://sursolver.com) client. One `await` submits the captcha and returns
the token or cookie. Runs on .NET Framework 4.6.2+, .NET 8+ and every `netstandard2.0` host.

```powershell
dotnet add package SurSolver
```

## Quick start

```csharp
using System.Text.Json.Nodes;
using SurSolver;

const string ApiKey = "YOUR_API_KEY";
const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36";

var client = new SurSolverClient(ApiKey);
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.TurnstileTokenProxyLess,
    ["websiteURL"] = "https://example.com/login",
    ["websiteKey"] = "0x4AAAAAAA...",
    ["userAgent"] = UserAgent,
});
Console.WriteLine(result.Solution["token"]);
```

Send the same `userAgent` with the request that uses the result. Tokens and cookies are
single-use, so use them right away. Create one `SurSolverClient` and reuse it; the examples below
reuse `client` and `UserAgent`.

## Examples

### Solve reCAPTCHA v3 in C#

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.ReCaptchaV3TokenProxyLess,
    ["websiteURL"] = "https://example.com/login",
    ["websiteKey"] = "6Le-wvkS...",
    ["pageAction"] = "login",
    ["userAgent"] = UserAgent,
});
Console.WriteLine(result.Solution["gRecaptchaResponse"]);
```

For reCAPTCHA v3 Enterprise use `TaskTypes.ReCaptchaV3EnterpriseTokenProxyLess`.

### Solve Cloudflare Turnstile

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.TurnstileTokenProxyLess,
    ["websiteURL"] = "https://example.com/signup",
    ["websiteKey"] = "0x4AAAAAAA...",
    ["userAgent"] = UserAgent,
});
// Submit the token as the cf-turnstile-response form field.
Console.WriteLine(result.Solution["token"]);
```

### Bypass the Cloudflare Challenge page (cf_clearance)

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.CloudflareChallengeCookie,
    ["websiteURL"] = "https://example.com/protected",
    ["proxy"] = "http://user:password@203.0.113.10:8000",
    ["userAgent"] = UserAgent,
});
Console.WriteLine(result.Solution["cf_clearance"]);
```

Replay the cookie through the same proxy and with the same `userAgent`.

### Solve GeeTest v4

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.GeeTestTokenProxyLess,
    ["websiteURL"] = "https://example.com/login",
    ["captchaId"] = "e392e1d7fd421dc63325744d5a2b9c73",
    ["userAgent"] = UserAgent,
});
Console.WriteLine($"{result.Solution["lot_number"]} {result.Solution["pass_token"]}");
```

GeeTest v3: send `gt` and a fresh `challenge` instead of `captchaId`.

### Solve the DataDome slider

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.DataDomeSliderCookie,
    ["websiteURL"] = "https://example.com/",
    ["captchaUrl"] = "https://geo.captcha-delivery.com/captcha/?initialCid=...",
    ["proxy"] = "http://user:password@203.0.113.10:8000",
    ["userAgent"] = UserAgent,
});
Console.WriteLine(result.Solution["datadome"]);
```

### Get an AWS WAF token (silent challenge)

```csharp
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.AwsChallengeCookie,
    ["websiteURL"] = "https://example.com/",
    ["scriptUrl"] = "https://example.token.awswaf.com/example/challenge.js",
    ["userAgent"] = UserAgent,
});
Console.WriteLine(result.Solution["aws-waf-token"]);
```

The visual AWS WAF CAPTCHA is `TaskTypes.AwsCaptchaCookie` and needs your proxy.

### Solve an image captcha (image to text)

```csharp
var image = await File.ReadAllBytesAsync("captcha.png");
var result = await client.SolveAsync(new JsonObject
{
    ["type"] = TaskTypes.ImageToTextTask,
    ["module"] = "1",
    ["body"] = Convert.ToBase64String(image),
});
Console.WriteLine(result.Solution["answer"]);
```

## Supported captchas

| Captcha | `task.type` | Guide |
|---|---|---|
| reCAPTCHA v3 | `ReCaptchaV3TokenProxyLess` | [recaptcha-v3](https://sursolver.com/solvers/recaptcha-v3) |
| reCAPTCHA v3 Enterprise | `ReCaptchaV3EnterpriseTokenProxyLess` | [recaptcha-v3-enterprise](https://sursolver.com/solvers/recaptcha-v3-enterprise) |
| Cloudflare Turnstile | `TurnstileTokenProxyLess` | [turnstile](https://sursolver.com/solvers/turnstile) |
| Cloudflare Challenge | `CloudflareChallengeCookie` | [cloudflare-challenge](https://sursolver.com/solvers/cloudflare-challenge) |
| GeeTest v3 / v4 | `GeeTestTokenProxyLess` | [geetest](https://sursolver.com/solvers/geetest) |
| DataDome | `DataDomeSliderCookie` | [datadome](https://sursolver.com/solvers/datadome) |
| AWS WAF Challenge | `AwsChallengeCookie` | [aws-challenge](https://sursolver.com/solvers/aws-challenge) |
| AWS WAF CAPTCHA | `AwsCaptchaCookie` | [aws-captcha](https://sursolver.com/solvers/aws-captcha) |
| Akamai (`_abck`) | `AkamaiCookieProxyLess` | [akamai](https://sursolver.com/solvers/akamai) |
| Akamai SBSD | `AkamaiSbsdCookie` | [akamai-sbsd](https://sursolver.com/solvers/akamai-sbsd) |
| Akamai BMP (Android) | `AkamaiBmpTask` | [akamai-bmp](https://sursolver.com/solvers/akamai-bmp) |
| Kasada | `KasadaTokenProxyLess` | [kasada](https://sursolver.com/solvers/kasada) |
| Shape | `ShapeCookieProxyLess` | [shape](https://sursolver.com/solvers/shape) |
| Imperva (reese84 / utmvc) | `ImpervaCookie`, `ImpervaUtmvcCookie` | [imperva](https://sursolver.com/solvers/imperva) |
| Vercel Security Checkpoint | `VercelCookie` | [vercel](https://sursolver.com/solvers/vercel) |
| FriendlyCaptcha | `FriendlyCaptchaTokenProxyLess` | [friendlycaptcha](https://sursolver.com/solvers/friendlycaptcha) |
| MTCaptcha | `MtCaptchaTokenProxyLess` | [mtcaptcha](https://sursolver.com/solvers/mtcaptcha) |
| Prosopo | `ProsopoTokenProxyLess` | [prosopo](https://sursolver.com/solvers/prosopo) |
| CaptchaFox | `CaptchaFoxToken` | [captchafox](https://sursolver.com/solvers/captchafox) |
| Altcha | `AltchaTokenProxyLess` | [altcha](https://sursolver.com/solvers/altcha) |
| Lemin | `LeminTokenProxyLess` | [lemin](https://sursolver.com/solvers/lemin) |
| Castle (Android) | `CastleAndroidToken` | [castle-android](https://sursolver.com/solvers/castle-android) |
| Image to Text | `ImageToTextTask` | [image-to-text](https://sursolver.com/solvers/image-to-text) |

Each guide lists the fields to send and where to find them on the page. The live list is
`await client.ListTaskTypesAsync()`; prices are `await client.ListServicesAsync()`.

## Errors

```csharp
try
{
    var result = await client.SolveAsync(task, cancellationToken: cancellationToken);
}
catch (NoSlotAvailableException)
{
    // all your threads are busy: try again in a moment
}
catch (TaskFailedException)
{
    // this captcha could not be solved; its charge is refunded
}
```

Other errors: `SurSolver.AuthenticationException` (wrong API key), `InvalidTaskException` (a
field is missing or wrong), `InsufficientBalanceException` (top up), `SolveTimeoutException` (no
result in time). Cancel with the `CancellationToken` you pass in.

## Links

- [Documentation](https://sursolver.com/docs) and [API reference](https://sursolver.com/docs/api)
- [Proxies guide](https://sursolver.com/docs/guides/proxies) and [error codes](https://sursolver.com/docs/guides/errors)
- [Pricing](https://sursolver.com/pricing)

## License

MIT
