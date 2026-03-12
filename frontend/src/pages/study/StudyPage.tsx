import { useState } from 'react';
import { analyzeStudy, explainStudy } from '@/api/study';
import type { StudyAnalyzeResponse } from '@/api/study';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { ScoreHeader } from '@/components/ScoreHeader';
import { TierBreakdown } from '@/components/TierBreakdown';

type Step = 1 | 2 | 3;

function StepIndicator({ current }: { current: Step }) {
    const steps = ['Input', 'Match Analysis', 'Blind Comparison'];
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

    const [resumeText, setResumeText] = useState('');
    const [jobText, setJobText] = useState('');
    const [analyzeLoading, setAnalyzeLoading] = useState(false);
    const [analyzeError, setAnalyzeError] = useState<string | null>(null);

    const [result, setResult] = useState<StudyAnalyzeResponse | null>(null);

    const [explanation, setExplanation] = useState<string | null>(null);
    const [explainLoading, setExplainLoading] = useState(false);
    const [explainError, setExplainError] = useState<string | null>(null);

    const handleAnalyze = async () => {
        setAnalyzeLoading(true);
        setAnalyzeError(null);
        setResult(null);
        setExplanation(null);
        try {
            const data = await analyzeStudy(resumeText, jobText);
            setResult(data);
            setStep(2);
        } catch (err) {
            setAnalyzeError(err instanceof Error ? err.message : 'Analysis failed. Please try again.');
        } finally {
            setAnalyzeLoading(false);
        }
    };

    const handleExplain = async () => {
        if (!result) return;
        setExplainLoading(true);
        setExplainError(null);
        try {
            const data = await explainStudy(
                resumeText,
                jobText,
                result.sessionResumeKey,
                result.sessionJobKey
            );
            setExplanation(data.explanation);
        } catch (err) {
            setExplainError(err instanceof Error ? err.message : 'Failed to generate explanation.');
        } finally {
            setExplainLoading(false);
        }
    };

    const handleReset = () => {
        setStep(1);
        setResumeText('');
        setJobText('');
        setAnalyzeError(null);
        setResult(null);
        setExplanation(null);
        setExplainError(null);
    };

    return (
        <div className="min-h-screen bg-slate-50">
            <div className="max-w-4xl mx-auto px-4 py-10 space-y-8">

                <div className="text-center space-y-2">
                    <h1 className="text-3xl font-bold text-slate-900 tracking-tight">ARIS User Study</h1>
                    <p className="text-slate-500 max-w-xl mx-auto text-sm">
                        Thank you for participating. Follow the steps below, then return to SurveyMonkey to complete the survey.
                    </p>
                </div>

                <StepIndicator current={step} />

                {step === 1 && (
                    <Card>
                        <CardHeader className="pb-2">
                            <CardTitle className="text-base font-semibold text-slate-800">Step 1 — Enter Your Resume and Job Description</CardTitle>
                            <p className="text-sm text-slate-500 mt-1">
                                Paste the full text of your resume and the job description you want to target, then click Analyze.
                            </p>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {analyzeLoading ? (
                                <Spinner label="Analyzing your resume against the job... this may take 30–60 seconds" />
                            ) : (
                                <>
                                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                        <div className="space-y-1.5">
                                            <label className="text-sm font-medium text-slate-700">Your Resume</label>
                                            <Textarea
                                                placeholder="Paste your resume text here..."
                                                className="min-h-64 resize-y bg-white"
                                                value={resumeText}
                                                onChange={e => setResumeText(e.target.value)}
                                            />
                                        </div>
                                        <div className="space-y-1.5">
                                            <label className="text-sm font-medium text-slate-700">Job Description</label>
                                            <Textarea
                                                placeholder="Paste the job description here..."
                                                className="min-h-64 resize-y bg-white"
                                                value={jobText}
                                                onChange={e => setJobText(e.target.value)}
                                            />
                                        </div>
                                    </div>
                                    {analyzeError && (
                                        <p className="text-sm text-red-600">{analyzeError}</p>
                                    )}
                                    <div className="flex justify-end">
                                        <Button
                                            onClick={handleAnalyze}
                                            disabled={resumeText.trim().length < 50 || jobText.trim().length < 50}
                                            className="bg-slate-900 hover:bg-slate-800 text-white px-8"
                                        >
                                            Analyze
                                        </Button>
                                    </div>
                                </>
                            )}
                        </CardContent>
                    </Card>
                )}

                {step === 2 && result && (
                    <div className="space-y-4">
                        <ScoreHeader
                            arisScore={result.arisScore}
                            vectorSimilarity={result.vectorSimilarity}
                        />

                        <TierBreakdown
                            matchingSkills={result.matchingSkills}
                            implicitlyDiscoveredSkills={result.implicitlyDiscoveredSkills}
                            prerequisiteMetSkills={result.prerequisiteMetSkills}
                            bridgeableSkills={result.bridgeableSkills}
                            hardGaps={result.hardGaps}
                        />

                        {explanation && (
                            <Card className="border-slate-200 bg-slate-50">
                                <CardContent className="pt-4">
                                    <p className="text-sm text-slate-700 leading-relaxed">{explanation}</p>
                                </CardContent>
                            </Card>
                        )}

                        {explainError && (
                            <p className="text-sm text-red-600">{explainError}</p>
                        )}

                        <div className="flex items-center justify-between pt-2">
                            <Button
                                variant="outline"
                                onClick={handleExplain}
                                disabled={explainLoading || explanation !== null}
                                className="text-sm"
                            >
                                {explainLoading ? 'Explaining...' : 'Explain This Score'}
                            </Button>

                            <Button
                                onClick={() => setStep(3)}
                                className="bg-slate-900 hover:bg-slate-800 text-white px-8"
                            >
                                Compare Tailored Resumes →
                            </Button>
                        </div>
                    </div>
                )}

                {step === 3 && result && (
                    <div className="space-y-6">
                        <div className="text-center space-y-1">
                            <h2 className="text-xl font-semibold text-slate-900">Which resume would you submit?</h2>
                            <p className="text-sm text-slate-500">Read both carefully before making your selection in the survey.</p>
                        </div>

                        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 items-start">
                            <Card className="flex flex-col border-slate-200">
                                <CardHeader className="pb-2">
                                    <CardTitle className="text-sm font-semibold text-slate-800">Resume A</CardTitle>
                                </CardHeader>
                                <CardContent className="flex-1">
                                    <div className="max-h-[600px] overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">
                                        {result.arisResume}
                                    </div>
                                </CardContent>
                            </Card>

                            <Card className="flex flex-col border-slate-200">
                                <CardHeader className="pb-2">
                                    <CardTitle className="text-sm font-semibold text-slate-800">Resume B</CardTitle>
                                </CardHeader>
                                <CardContent className="flex-1">
                                    <div className="max-h-[600px] overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">
                                        {result.chatGptResume}
                                    </div>
                                </CardContent>
                            </Card>
                        </div>

                        <p className="text-xs text-slate-400 text-center">
                            Record your preference in the survey before continuing.
                        </p>

                        <div className="flex items-center justify-between">
                            <button
                                onClick={() => setStep(2)}
                                className="text-sm text-slate-400 hover:text-slate-600"
                            >
                                ← Back to Analysis
                            </button>
                            <Button variant="outline" onClick={handleReset} className="px-8 text-sm">
                                Start Over
                            </Button>
                        </div>
                    </div>
                )}

            </div>
        </div>
    );
}
