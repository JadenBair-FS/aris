// Auth is supplied via setTokenGetter

let _getToken: (() => Promise<string | null>) | null = null;
let _signOut: (() => Promise<void>) | null = null;

export function setTokenGetter(fn: () => Promise<string | null>) {
    _getToken = fn;
}

export function setSignOut(fn: () => Promise<void>) {
    _signOut = fn;
}

/** Fetches a binary response (Blob) with the same auth token injection as apiClient. */
export async function fetchBlobWithAuth(endpoint: string, options: RequestInit = {}): Promise<Response> {
    const url = endpoint.startsWith('http') ? endpoint : `/api${endpoint}`;
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };

    if (_getToken) {
        try {
            const token = await _getToken();
            if (token) headers['Authorization'] = `Bearer ${token}`;
        } catch (e) {
            console.error('Failed to retrieve Clerk token:', e);
        }
    }

    return fetch(url, { ...options, headers });
}

export async function apiClient<T>(
    endpoint: string,
    options: RequestInit = {}
): Promise<T> {
    const url = endpoint.startsWith('http') ? endpoint : `/api${endpoint}`;

    const headers: Record<string, string> = {};
    if (options.headers) {
        Object.entries(options.headers).forEach(([key, value]) => {
            headers[key] = value as string;
        });
    }

    if (!(options.body instanceof FormData)) {
        headers['Content-Type'] = 'application/json';
    }

    // Auth Injection
    if (_getToken) {
        try {
            const token = await _getToken();
            if (token) {
                headers['Authorization'] = `Bearer ${token}`;
            }
        } catch (e) {
            console.error("Failed to retrieve Clerk token:", e);
        }
    }

    const startTime = performance.now();
    let requestBody = options.body;
    if (options.body && typeof options.body === 'string') {
        try {
            requestBody = JSON.parse(options.body);
        } catch (e) {
            // Ignored
        }
    }

    try {
        const response = await fetch(url, { ...options, headers });
        const elapsedMs = Math.round(performance.now() - startTime);

        let data;
        const contentType = response.headers.get('content-type');
        if (contentType && contentType.includes('application/json')) {
            data = await response.json();
        } else {
            data = await response.text();
        }

        window.dispatchEvent(
            new CustomEvent('aris-debug-log', {
                detail: {
                    label: `${options.method || 'GET'} ${endpoint}`,
                    request: requestBody,
                    response: data,
                    elapsedMs,
                },
            })
        );

        if (response.status === 401) {
            if (_signOut) await _signOut();
            window.location.href = '/login';
            throw new Error('Session expired. Please log in again.');
        }

        if (!response.ok) {
            throw new Error(data?.message || data || `Error HTTP ${response.status}`);
        }

        return data as T;
    } catch (error: any) {
        const elapsedMs = Math.round(performance.now() - startTime);
        window.dispatchEvent(
            new CustomEvent('aris-debug-log', {
                detail: {
                    label: `${options.method || 'GET'} ${endpoint} [ERROR]`,
                    request: requestBody,
                    response: { error: error.message },
                    elapsedMs,
                },
            })
        );
        throw error;
    }
}
