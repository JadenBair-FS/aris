import { useState, useEffect } from 'react';

interface UploadProgressProps {
    isVisible: boolean;
    phases: string[];
}

export function UploadProgress({ isVisible, phases }: UploadProgressProps) {
    const [phaseIndex, setPhaseIndex] = useState(0);
    const [show, setShow] = useState(false);

    useEffect(() => {
        if (isVisible) {
            setPhaseIndex(0);
            setShow(true);
        } else {
            setShow(false);
        }
    }, [isVisible]);

    useEffect(() => {
        if (!isVisible) return;
        const interval = setInterval(() => {
            setPhaseIndex(i => (i + 1) % phases.length);
        }, 2800);
        return () => clearInterval(interval);
    }, [isVisible, phases.length]);

    if (!isVisible) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-white/80 backdrop-blur-sm">
            <div className="flex flex-col items-center gap-6">
                {/* Spinner ring */}
                <div className="w-12 h-12 rounded-full border-4 border-slate-900 border-r-transparent animate-spin" />

                {/* Phase text */}
                <div
                    className={`flex flex-col items-center gap-1 transition-all duration-500 ${show ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-1'}`}
                >
                    <p className="text-sm font-medium text-slate-900">{phases[phaseIndex]}</p>
                    <p className="text-sm text-slate-400">This may take up to 30 seconds</p>
                </div>
            </div>
        </div>
    );
}

export const RESUME_PHASES = [
    'Parsing your resume...',
    'Extracting skills with AI...',
    'Grounding against the knowledge graph...',
    'Generating semantic embeddings...',
    'Saving your profile...',
];

export const JOB_PHASES = [
    'Parsing job description...',
    'Extracting required skills with AI...',
    'Grounding skills against knowledge graph...',
    'Generating semantic embeddings...',
    'Saving job posting...',
];
