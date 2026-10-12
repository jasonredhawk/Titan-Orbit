const JSON_HEADERS = { "content-type": "application/json; charset=utf-8" };

export function json(body: unknown, status = 200, extra?: HeadersInit): Response {
  const headers = new Headers(JSON_HEADERS);
  if (extra) {
    const more = new Headers(extra);
    more.forEach((value, key) => headers.set(key, value));
  }
  return new Response(JSON.stringify(body), { status, headers });
}

/** Browser game origin, or null when the caller is not a browser. */
export function allowedOrigin(request: Request, allowedList: string): string | null {
  const origin = request.headers.get("Origin");
  if (!origin)
    return null;
  const allowed = allowedList.split(",").map((item) => item.trim()).filter(Boolean);
  return allowed.includes(origin) ? origin : "";
}

export function corsHeaders(origin: string | null, request?: Request): HeadersInit {
  if (!origin)
    return {};
  return {
    "access-control-allow-origin": origin,
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": allowHeaders(request),
    "access-control-max-age": "120",
    vary: "Origin",
  };
}

/** Unity WebGL adds x-unity-version, so the preflight must name it or the browser blocks checkout. */
function allowHeaders(request?: Request): string {
  const requested = request?.headers.get("Access-Control-Request-Headers") ?? "";
  if (/^[A-Za-z0-9_,\- ]{1,200}$/.test(requested))
    return requested;
  return "content-type, x-unity-version";
}
