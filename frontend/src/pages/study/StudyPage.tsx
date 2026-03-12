import { useState } from 'react';
import { prepareResume, prepareJob, generateComparison, tailorResume } from '@/api/study';
import type { StudyCompareResponse } from '@/api/study';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

type Step = 1 | 2 | 3;

function StepIndicator({ current }: { current: Step }) {
    const steps = ['Your Resume', 'Job Description', 'Compare'];
    return (
        <div className="flex items-center justify-center gap-0">
            {steps.map((label, i) => {
                const num = (i + 1) as Step;
                const done = num < current;
                const active = num === current;
                return (
                    <div key={num} className="flex items-center">
                        <div className="flex flex-col items-center gap-1">
                            <div className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-semibold transition-colors
                                ${done ? 'bg-green-500 text-white' : active ? 'bg-slate-900 text-white' : 'bg-slate-200 text-slate-400'}`}>
                                {done ? '✓' : num}
                            </div>
                            <span className={`text-xs whitespace-nowrap ${active ? 'text-slate-700 font-medium' : 'text-slate-400'}`}>
                                {label}
                            </span>
                        </div>
                        {i < steps.length - 1 && (
                            <div className={`w-16 h-0.5 mb-4 mx-1 ${done ? 'bg-green-500' : 'bg-slate-200'}`} />
                        )}
                    </div>
                );
            })}
        </div>
    );
}

function Spinner({ label }: { label: string }) {
    return (
        <div className="flex flex-col items-center gap-3 py-10">
            <svg className="animate-spin h-6 w-6 text-slate-400" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
            </svg>
            <p className="text-sm text-slate-500">{label}</p>
        </div>
    );
}

export default function StudyPage() {
    const [step, setStep] = useState<Step>(1);

    // Step 1 state
    const [resumeText, setResumeText] = useState('');
    const [resumeKey, setResumeKey] = useState<string | null>(null);
    const [resumeLoading, setResumeLoading] = useState(false);
    const [resumeError, setResumeError] = useState<string | null>(null);

    // Step 2 state
    const [jobText, setJobText] = useState('');
    const [jobKey, setJobKey] = useState<string | null>(null);
    const [jobLoading, setJobLoading] = useState(false);
    const [jobError, setJobError] = useState<string | null>(null);

    // Step 3 state
    const [result, setResult] = useState<StudyCompareResponse | null>(null);
    const [generateLoading, setGenerateLoading] = useState(false);
    const [generateError, setGenerateError] = useState<string | null>(null);

    // Tailor state
    const [tailoredText, setTailoredText] = useState<string | null>(null);
    const [tailorLoading, setTailorLoading] = useState(false);
    const [tailorError, setTailorError] = useState<string | null>(null);

    const handleResumeNext = async () => {
        setResumeLoading(true);
        setResumeError(null);
        try {
            const { sessionKey } = await prepareResume(resumeText);
            setResumeKey(sessionKey);
            setStep(2);
        } catch (err) {
            setResumeError(err instanceof Error ? err.message : 'Failed to process resume. Please try again.');
        } finally {
            setResumeLoading(false);
        }
    };

    const handleJobNext = async () => {
        setJobLoading(true);
        setJobError(null);
        try {
            const { sessionKey } = await prepareJob(jobText);
            setJobKey(sessionKey);
            setStep(3);
        } catch (err) {
            setJobError(err instanceof Error ? err.message : 'Failed to process job description. Please try again.');
        } finally {
            setJobLoading(false);
        }
    };

    const handleGenerate = async () => {
        if (!resumeKey || !jobKey) return;
        setGenerateLoading(true);
        setGenerateError(null);
        setResult(null);
        setTailoredText(null);
        try {
            const data = await generateComparison(resumeKey, jobKey, resumeText, jobText);
            setResult(data);
        } catch (err) {
            const msg = err instanceof Error ? err.message : 'Failed to generate responses.';
            setGenerateError(msg);
            // If session expired, send user back to the right step
            if (msg.toLowerCase().includes('step 1')) setStep(1);
            else if (msg.toLowerCase().includes('step 2')) setStep(2);
        } finally {
            setGenerateLoading(false);
        }
    };

    const handleTailor = async () => {
        setTailorLoading(true);
        setTailorError(null);
        setTailoredText(null);
        try {
            const data = await tailorResume(resumeText, jobText, resumeKey ?? undefined, jobKey ?? undefined);
            setTailoredText(data.tailoredText);
        } catch (err) {
            setTailorError(err instanceof Error ? err.message : 'Failed to generate tailored resume.');
        } finally {
            setTailorLoading(false);
        }
    };

    const handleReset = () => {
        setStep(1);
        setResumeText(''); setResumeKey(null); setResumeError(null);
        setJobText(''); setJobKey(null); setJobError(null);
        setResult(null); setGenerateError(null);
        setTailoredText(null); setTailorError(null);
    };

    return (
        <div className="min-h-screen bg-slate-50">
            <div className="max-w-3xl mx-auto px-4 py-10 space-y-8">

                {/* Header */}
                <div className="text-center space-y-2">
                    <h1 className="text-3xl font-bold text-slate-900 tracking-tight">ARIS User Study</h1>
                    <p className="text-slate-500 max-w-xl mx-auto text-sm">
                        Thank you for participating. Follow the steps below, then return to SurveyMonkey to complete the survey.
                    </p>
                </div>

                <StepIndicator current={step} />

                {/* ── Step 1: Resume ── */}
                {step === 1 && (
                    <Card>
                        <CardHeader className="pb-2">
                            <CardTitle className="text-base font-semibold text-slate-800">Step 1 — Paste Your Resume</CardTitle>
                            <p className="text-sm text-slate-500 mt-1">
                                Copy and paste the full text of your resume below. We will extract your skills and experience.
                            </p>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {resumeLoading ? (
                                <Spinner label="Extracting resume details... this may take 20–30 seconds" />
                            ) : (
                                <>
                                    <Textarea
                                        placeholder="Paste your resume text here..."
                                        className="min-h-64 resize-y bg-white"
                                        value={resumeText}
                                        onChange={e => setResumeText(e.target.value)}
                                    />
                                    {resumeError && (
                                        <p className="text-sm text-red-600">{resumeError}</p>
                                    )}
                                    <div className="flex justify-end">
                                        <Button
                                            onClick={handleResumeNext}
                                            disabled={resumeText.trim().length < 50}
                                            className="bg-slate-900 hover:bg-slate-800 text-white px-8"
                                        >
                                            Next →
                                        </Button>
                                    </div>
                                </>
                            )}
                        </CardContent>
                    </Card>
                )}

                {/* ── Step 2: Job Description ── */}
                {step === 2 && (
                    <Card>
                        <CardHeader className="pb-2">
                            <CardTitle className="text-base font-semibold text-slate-800">Step 2 — Paste a Job Description</CardTitle>
                            <p className="text-sm text-slate-500 mt-1">
                                Find a job you are interested in and paste the full job description below.
                            </p>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {jobLoading ? (
                                <Spinner label="Extracting job requirements... this may take 20–30 seconds" />
                            ) : (
                                <>
                                    <Textarea
                                        placeholder="Paste the job description here..."
                                        className="min-h-64 resize-y bg-white"
                                        value={jobText}
                                        onChange={e => setJobText(e.target.value)}
                                    />
                                    {jobError && (
                                        <p className="text-sm text-red-600">{jobError}</p>
                                    )}
                                    <div className="flex items-center justify-between">
                                        <button
                                            onClick={() => setStep(1)}
                                            className="text-sm text-slate-400 hover:text-slate-600"
                                        >
                                            ← Back
                                        </button>
                                        <Button
                                            onClick={handleJobNext}
                                            disabled={jobText.trim().length < 50}
                                            className="bg-slate-900 hover:bg-slate-800 text-white px-8"
                                        >
                                            Next →
                                        </Button>
                                    </div>
                                </>
                            )}
                        </CardContent>
                    </Card>
                )}

                {/* ── Step 3: Compare ── */}
                {step === 3 && (
                    <div className="space-y-4">
                        {!result && !generateLoading && (
                            <Card>
                                <CardHeader className="pb-2">
                                    <CardTitle className="text-base font-semibold text-slate-800">Step 3 — Compare AI Systems</CardTitle>
                                    <p className="text-sm text-slate-500 mt-1">
                                        Your resume and job description have been processed. Click below to generate two AI responses side by side.
                                        Read both carefully before returning to SurveyMonkey.
                                    </p>
                                </CardHeader>
                                <CardContent className="space-y-4">
                                    {generateError && (
                                        <p className="text-sm text-red-600">{generateError}</p>
                                    )}
                                    <div className="flex items-center justify-between">
                                        <button
                                            onClick={() => setStep(2)}
                                            className="text-sm text-slate-400 hover:text-slate-600"
                                        >
                                            ← Back
                                        </button>
                                        <Button
                                            onClick={handleGenerate}
                                            className="bg-slate-900 hover:bg-slate-800 text-white px-8"
                                        >
                                            Generate Responses
                                        </Button>
                                    </div>
                                </CardContent>
                            </Card>
                        )}

                        {generateLoading && (
                            <Spinner label="Generating AI responses... this may take 30–40 seconds" />
                        )}

                        {result && !generateLoading && (
                            <>
                                {/* Side-by-side results */}
                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4 items-start">
                                    <Card className="flex flex-col border-slate-200">
                                        <CardHeader className="pb-2">
                                            <CardTitle className="text-sm font-semibold text-slate-800">System 1</CardTitle>
                                        </CardHeader>
                                        <CardContent className="flex-1">
                                            <div className="max-h-96 overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">
                                                {result.ragResponse}
                                            </div>
                                        </CardContent>
                                    </Card>

                                    <Card className="flex flex-col border-slate-200">
                                        <CardHeader className="pb-2">
                                            <CardTitle className="text-sm font-semibold text-slate-800">System 2</CardTitle>
                                        </CardHeader>
                                        <CardContent className="flex-1">
                                            <div className="max-h-96 overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">
                                                {result.graphRagResponse}
                                            </div>
                                        </CardContent>
                                    </Card>
                                </div>

                                {/* Tailor Resume section */}
                                <div className="pt-2 border-t border-slate-200 space-y-3">
                                    <div className="flex flex-col items-center gap-2 text-center">
                                        <p className="text-sm text-slate-500 max-w-md">
                                            Optionally, generate a tailored version of your resume for this role.
                                            Note your impressions — you will be asked about this in the survey.
                                        </p>
                                        <Button
                                            onClick={handleTailor}
                                            disabled={tailorLoading}
                                            variant="outline"
                                            className="px-8 text-sm"
                                        >
                                            {tailorLoading ? 'Tailoring resume... this may take 30–40 seconds' : 'Tailor Resume'}
                                        </Button>
                                    </div>

                                    {tailorError && (
                                        <p className="text-sm text-red-600 text-center">{tailorError}</p>
                                    )}

                                    {tailoredText && (
                                        <Card className="border-slate-200 bg-slate-50">
                                            <CardHeader className="pb-2">
                                                <CardTitle className="text-sm font-semibold text-slate-700">Tailored Resume</CardTitle>
                                            </CardHeader>
                                            <CardContent>
                                                <div className="max-h-[480px] overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed font-mono">
                                                    {tailoredText}
                                                </div>
                                            </CardContent>
                                        </Card>
                                    )}
                                </div>

                                {/* Reset */}
                                <div className="flex justify-center pt-2">
                                    <Button variant="outline" onClick={handleReset} className="px-8 text-sm">
                                        Start Over
                                    </Button>
                                </div>
                            </>
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}
