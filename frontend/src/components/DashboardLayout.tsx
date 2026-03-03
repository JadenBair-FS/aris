import { Outlet, Link, useLocation } from 'react-router';
import { useAuth } from '../context/AuthContext';
import { DebugPanel } from './DebugPanel';
import { Avatar, AvatarFallback, AvatarImage } from './ui/avatar';
import { House, User, Upload, Briefcase, Plus, LogOut, Menu } from 'lucide-react';
import { useState } from 'react';
import { Sheet, SheetContent, SheetTrigger } from './ui/sheet';
import { Button } from './ui/button';

export function DashboardLayout() {
    const { user, logout } = useAuth();
    const location = useLocation();
    const [isMobileOpen, setIsMobileOpen] = useState(false);

    if (!user) return null;

    const isRecruiter = user.role === 'recruiter';

    const seekerLinks = [
        { href: '/home', label: 'Home', icon: House },
        { href: '/profile', label: 'My Profile', icon: User },
        { href: '/profile/upload', label: 'Upload Resume', icon: Upload },
    ];

    const recruiterLinks = [
        { href: '/home', label: 'Home', icon: House },
        { href: '/profile/jobs', label: 'My Jobs', icon: Briefcase },
        { href: '/profile/jobs/upload', label: 'Post a Job', icon: Plus },
    ];

    const links = isRecruiter ? recruiterLinks : seekerLinks;

    const isActive = (href: string) => {
        if (href === '/home') return location.pathname === '/home';
        return location.pathname === href || location.pathname.startsWith(href + '/');
    };

    const NavLinks = () => (
        <nav className="flex-1 py-4 px-3 space-y-1 overflow-y-auto">
            {links.map((link) => {
                const active = isActive(link.href);
                return (
                    <Link
                        key={link.href}
                        to={link.href}
                        onClick={() => setIsMobileOpen(false)}
                        className={`flex items-center gap-3 px-3 py-2 rounded-lg text-sm transition-colors ${
                            active
                                ? 'bg-slate-100 text-slate-900 font-medium'
                                : 'text-slate-600 hover:bg-slate-50'
                        }`}
                    >
                        <link.icon className="h-4 w-4 shrink-0" />
                        {link.label}
                    </Link>
                );
            })}
        </nav>
    );

    const UserFooter = () => (
        <div className="p-4 border-t border-slate-100 flex items-center justify-between">
            <div className="flex items-center gap-3 min-w-0">
                <Avatar className="h-8 w-8 shrink-0">
                    <AvatarImage src={user.avatarUrl} />
                    <AvatarFallback className="bg-slate-200 text-slate-700 text-xs font-medium">
                        {user.name.charAt(0).toUpperCase()}
                    </AvatarFallback>
                </Avatar>
                <div className="min-w-0">
                    <p className="text-sm font-medium text-slate-900 truncate">{user.name}</p>
                    <p className="text-xs text-slate-500 truncate">{user.email}</p>
                </div>
            </div>
            <button
                onClick={() => logout()}
                title="Logout"
                className="shrink-0 ml-2 text-slate-400 hover:text-slate-600 transition-colors"
            >
                <LogOut className="h-4 w-4" />
            </button>
        </div>
    );

    return (
        <div className="flex min-h-screen bg-slate-50">
            {/* Mobile header */}
            <div className="md:hidden fixed top-0 w-full z-40 bg-white border-b border-slate-100 h-14 flex items-center px-4 justify-between">
                <span className="font-semibold text-slate-900">ARIS</span>
                <Sheet open={isMobileOpen} onOpenChange={setIsMobileOpen}>
                    <SheetTrigger asChild>
                        <Button variant="ghost" size="icon"><Menu className="h-5 w-5" /></Button>
                    </SheetTrigger>
                    <SheetContent side="left" className="p-0 w-[240px] flex flex-col bg-white pt-10">
                        <div className="px-5 pb-4 border-b border-slate-100 flex items-center justify-between">
                            <span className="font-semibold text-slate-900">ARIS</span>
                            <span className="bg-slate-100 text-slate-600 text-xs rounded-full px-2 py-0.5 capitalize">{user.role}</span>
                        </div>
                        <NavLinks />
                        <UserFooter />
                    </SheetContent>
                </Sheet>
            </div>

            {/* Desktop sidebar */}
            <aside className="hidden md:flex flex-col w-[240px] fixed h-screen z-30 border-r border-slate-100 bg-white">
                <div className="px-5 py-5 border-b border-slate-100 flex items-center justify-between">
                    <span className="font-semibold text-slate-900">ARIS</span>
                    <span className="bg-slate-100 text-slate-600 text-xs rounded-full px-2 py-0.5 capitalize">{user.role}</span>
                </div>
                <NavLinks />
                <UserFooter />
            </aside>

            {/* Main content */}
            <main className="flex-1 md:pl-[240px] pt-14 md:pt-0 min-h-screen">
                <div className="p-6 md:p-8">
                    <Outlet />
                </div>
            </main>

            <DebugPanel />
        </div>
    );
}
