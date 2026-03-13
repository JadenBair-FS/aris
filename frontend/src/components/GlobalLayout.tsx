import { Link, Outlet, useLocation } from 'react-router';
import { DebugPanel } from './DebugPanel';

export function GlobalLayout() {
    const location = useLocation();
    const isRecruiter = location.pathname.startsWith('/recruiter');
    const isSeeker = location.pathname.startsWith('/seeker');

    let modeLabel = '';
    if (isRecruiter) modeLabel = 'Recruiter';
    else if (isSeeker) modeLabel = 'Job Seeker';

    return (
        <div className="min-h-screen bg-background font-sans antialiased text-foreground pb-32">
            <header className="border-b bg-card w-full sticky top-0 z-40 shadow-sm">
                <div className="max-w-4xl mx-auto px-4 h-14 flex items-center justify-between">
                    <Link to="/" className="font-bold text-xl tracking-tight text-primary">ARIS</Link>
                    {modeLabel && <span className="text-sm font-medium text-muted-foreground bg-muted px-3 py-1 rounded-full">{modeLabel}</span>}
                </div>
            </header>
            <main className="max-w-4xl mx-auto px-4 py-8 w-full relative">
                <Outlet />
            </main>
            <DebugPanel />
        </div>
    );
}
