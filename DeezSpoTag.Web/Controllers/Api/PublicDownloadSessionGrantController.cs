using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// HTTPS return target for Zarz public-download verification when running as a web app.
/// SpotiFLAC Mobile intercepts <c>spotiflac://session-grant</c>; browsers cannot, so the
/// challenge <c>cb</c> points here and the page hands the grant back to the Login opener.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public-download")]
public sealed class PublicDownloadSessionGrantController : ControllerBase
{
    [HttpGet("session-grant")]
    [Produces("text/html")]
    public ContentResult SessionGrant()
    {
        // Grant may arrive as query (redirect) — hand off to opener and close the popup.
        const string html =
            """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Public download verification</title>
              <style>
                body { font-family: system-ui, sans-serif; background: #0b0f14; color: #e8eef5;
                       display: grid; place-items: center; min-height: 100vh; margin: 0; }
                main { max-width: 28rem; padding: 1.5rem; text-align: center; }
                p { color: #9aa7b5; line-height: 1.45; }
              </style>
            </head>
            <body>
              <main>
                <h1>Verification complete</h1>
                <p id="status">Returning to DeezSpoTag…</p>
              </main>
              <script>
                (function () {
                  function readGrant() {
                    try {
                      var params = new URLSearchParams(window.location.search || '');
                      var grant = params.get('grant');
                      if (grant) return grant;
                      if (window.location.hash) {
                        var hashParams = new URLSearchParams(String(window.location.hash).replace(/^#/, ''));
                        grant = hashParams.get('grant');
                        if (grant) return grant;
                      }
                    } catch (e) {
                      // Malformed query/hash — treat as missing grant.
                    }
                    return null;
                  }

                  var grant = readGrant();
                  var status = document.getElementById('status');
                  if (!grant) {
                    if (status) status.textContent = 'No grant was returned. You can close this window and try again.';
                    return;
                  }

                  try {
                    if (window.opener && !window.opener.closed) {
                      window.opener.postMessage({ type: 'zarz_grant', grant: grant }, window.location.origin);
                      if (status) status.textContent = 'Verified. You can close this window.';
                    } else if (status) {
                      status.textContent = 'Verified. Return to the Login tab if it did not update.';
                    }
                  } catch (e) {
                    if (status) status.textContent = 'Verified, but the opener could not be notified. Close this window and retry if needed.';
                  }

                  try {
                    window.close();
                  } catch (e) {
                    // Popup may already be closed by the browser.
                  }
                })();
              </script>
            </body>
            </html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }
}
