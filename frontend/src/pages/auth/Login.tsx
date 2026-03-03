import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useSignIn } from '@clerk/clerk-react';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Loader2 } from 'lucide-react';

export default function Login() {
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [errorText, setErrorText] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    const navigate = useNavigate();
    const { signIn, setActive, isLoaded } = useSignIn();

    const handleLogin = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!isLoaded) return;
        setErrorText('');
        setIsLoading(true);

        try {
            const result = await signIn.create({ identifier: email, password });
            if (result.status === 'complete') {
                await setActive({ session: result.createdSessionId });
                navigate('/home');
            } else {
                setErrorText('More verification needed.');
            }
        } catch (err: any) {
            setErrorText(err.errors?.[0]?.longMessage || err.message || 'Failed to sign in.');
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex">
            {/* Left brand panel */}
            <div className="hidden lg:flex flex-col justify-center px-12 w-[420px] shrink-0 bg-slate-900 text-white">
                <h1 className="text-3xl font-semibold tracking-tight mb-3">ARIS</h1>
                <p className="text-slate-400 text-sm leading-relaxed">
                    Automated Recruitment Intelligence System. AI-powered skill gap matching that connects the right people to the right roles.
                </p>
            </div>

            {/* Right form panel */}
            <div className="flex-1 flex items-center justify-center bg-white p-8">
                <div className="w-full max-w-sm">
                    <div className="mb-8">
                        <span className="lg:hidden font-semibold text-slate-900 text-xl">ARIS</span>
                        <h2 className="text-2xl font-semibold text-slate-900 mt-2">Welcome back</h2>
                        <p className="text-slate-500 text-sm mt-1">Sign in to your account</p>
                    </div>

                    <form onSubmit={handleLogin} className="space-y-4">
                        {errorText && (
                            <Alert variant="destructive">
                                <AlertDescription>{errorText}</AlertDescription>
                            </Alert>
                        )}

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Email address</label>
                            <Input
                                type="email"
                                placeholder="name@example.com"
                                value={email}
                                onChange={e => setEmail(e.target.value)}
                                required
                                disabled={isLoading}
                            />
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Password</label>
                            <Input
                                type="password"
                                placeholder="••••••••"
                                value={password}
                                onChange={e => setPassword(e.target.value)}
                                required
                                disabled={isLoading}
                            />
                        </div>

                        <Button
                            type="submit"
                            className="w-full bg-slate-900 hover:bg-slate-800 text-white mt-2"
                            disabled={isLoading}
                        >
                            {isLoading && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                            Sign In
                        </Button>
                    </form>

                    <p className="text-sm text-slate-500 text-center mt-6">
                        Don't have an account?{' '}
                        <Link to="/signup" className="text-slate-900 font-medium hover:underline">
                            Sign up
                        </Link>
                    </p>
                </div>
            </div>
        </div>
    );
}
