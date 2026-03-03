import { Link } from 'react-router';
import { Button } from '../components/ui/button';

export default function Landing() {
    return (
        <div className="min-h-screen bg-white flex items-center justify-center p-6">
            <div className="text-center max-w-md">
                <h1 className="text-4xl font-semibold text-slate-900 tracking-tight mb-3">ARIS</h1>
                <p className="text-slate-500 mb-10 text-base">
                    Automated Recruitment Intelligence System — AI-powered skill gap matching for job seekers and recruiters.
                </p>
                <div className="flex flex-col sm:flex-row gap-3 justify-center">
                    <Button asChild size="lg" className="bg-slate-900 text-white hover:bg-slate-800">
                        <Link to="/signup">Get Started</Link>
                    </Button>
                    <Button asChild size="lg" variant="outline">
                        <Link to="/login">Sign In</Link>
                    </Button>
                </div>
            </div>
        </div>
    );
}
