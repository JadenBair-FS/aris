import { useState } from 'react';
import { compareResponses, tailorResume } from '@/api/study';
import type { StudyCompareResponse } from '@/api/study';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

export default function StudyPage() {
    const [resumeText, setResumeText] = useState('');
    const [jobText, setJobText] = useState('');
    const [result, setResult] = useState<StudyCompareResponse | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const [tailoredText, setTailoredText] = useState<string | null>(null);
    const [isTailoring, setIsTailoring] = useState(false);
    const [tailorError, setTailorError] = useState<string | null>(null);

    const canSubmit = resumeText.trim().length > 0 && jobText.trim().length > 0 && !isLoading;

    const handleGenerate = async () => {
        if (!canSubmit) return;
        setIsLoading(true);
        setError(null);
        setResult(null);
        setTailoredText(null);
        try {
            const data = await compareResponses(resumeText, jobText);
            setResult(data);
        } catch (err) {
            setError(err instanceof Error ? err.message : 'An error occurred. Please try again.');
        } finally {
            setIsLoading(false);
        }
    };

    const handleTailor = async () => {
        setIsTailoring(true);
        setTailorError(null);
        setTailoredText(null);
        try {
            const data = await tailorResume(resumeText, jobText);
            setTailoredText(data.tailoredText);
        } catch (err) {
            setTailorError(err instanceof Error ? err.message : 'Failed to generate tailored resume.');
        } finally {
            setIsTailoring(false);
        }
    };

    const handleReset = () => {
        setResumeText('');
        setJobText('');
        setResult(null);
        setError(null);
        setTailoredText(null);
        setTailorError(null);
    };

    return (
        <div className="min-h-screen bg-slate-50">
            <div className="max-w-5xl mx-auto px-4 py-10 space-y-8">

                {/* Header */}
                <div className="text-center space-y-2">
                    <h1 className="text-3xl font-bold text-slate-900 tracking-tight">ARIS User Study</h1>
                    <p className="text-slate-500 max-w-xl mx-auto text-sm">
                        Thank you for participating in our research study. Please follow the instructions
                        from SurveyMonkey before proceeding.
                    </p>
                </div>

                {/* Instructions card */}
                <Card className="border-blue-200 bg-blue-50">
                    <CardHeader className="pb-2">
                        <CardTitle className="text-base font-semibold text-blue-900">Instructions</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <ol className="space-y-1 text-sm text-blue-800 list-decimal list-inside">
                            <li>Paste your resume text in the box below.</li>
                            <li>Paste the job description you are comparing against.</li>
                            <li>Click <strong>Generate Responses</strong> and read both responses carefully.</li>
                            <li>Optionally click <strong>Tailor Resume</strong> to see an AI-rewritten version of your resume.</li>
                            <li>Return to SurveyMonkey to complete the survey.</li>
                        </ol>
                    </CardContent>
                </Card>

                {/* Input section */}
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-slate-700">Your Resume</label>
                        <Textarea
                            placeholder="Paste your resume text here..."
                            className="min-h-48 resize-y bg-white"
                            value={resumeText}
                            onChange={e => setResumeText(e.target.value)}
                            disabled={isLoading || isTailoring}
                        />
                    </div>
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-slate-700">Job Description</label>
                        <Textarea
                            placeholder="Paste the job description here..."
                            className="min-h-48 resize-y bg-white"
                            value={jobText}
                            onChange={e => setJobText(e.target.value)}
                            disabled={isLoading || isTailoring}
                        />
                    </div>
                </div>

                {/* Generate button */}
                <div className="flex justify-center">
                    <Button
                        onClick={handleGenerate}
                        disabled={!canSubmit}
                        className="w-full md:w-auto px-10 bg-slate-900 hover:bg-slate-800 text-white text-sm font-semibold"
                        size="lg"
                    >
                        {isLoading ? 'Analyzing...' : 'Generate Responses'}
                    </Button>
                </div>

                {/* Loading state */}
                {isLoading && (
                    <div className="flex flex-col items-center gap-3 py-8">
                        <svg
                            className="animate-spin h-6 w-6 text-slate-400"
                            xmlns="http://www.w3.org/2000/svg"
                            fill="none"
                            viewBox="0 0 24 24"
                        >
                            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                        </svg>
                        <p className="text-sm text-slate-500">Analyzing your profile... this may take 30–60 seconds</p>
                    </div>
                )}

                {/* Error state */}
                {error && !isLoading && (
                    <p className="text-sm text-red-600 text-center">{error}</p>
                )}

                {/* Results */}
                {result && !isLoading && (
                    <div className="space-y-4">
                        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 items-start">

                            {/* Left: System 1 (RAG) */}
                            <Card className="flex flex-col border-slate-200">
                                <CardHeader className="pb-2">
                                    <CardTitle className="text-sm font-semibold text-slate-800">
                                        System 1
                                    </CardTitle>
                                </CardHeader>
                                <CardContent className="flex-1">
                                    <div className="max-h-96 overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">
                                        {result.ragResponse}
                                    </div>
                                </CardContent>
                            </Card>

                            {/* Right: System 2 (GraphRAG) */}
                            <Card className="flex flex-col border-slate-200">
                                <CardHeader className="pb-2">
                                    <CardTitle className="text-sm font-semibold text-slate-800">
                                        System 2
                                    </CardTitle>
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
                                    disabled={isTailoring}
                                    variant="outline"
                                    className="px-8 text-sm"
                                >
                                    {isTailoring ? 'Tailoring resume... this may take 30–60 seconds' : 'Tailor Resume'}
                                </Button>
                            </div>

                            {tailorError && (
                                <p className="text-sm text-red-600 text-center">{tailorError}</p>
                            )}

                            {tailoredText && (
                                <Card className="border-slate-200 bg-slate-50">
                                    <CardHeader className="pb-2">
                                        <CardTitle className="text-sm font-semibold text-slate-700">
                                            Tailored Resume
                                        </CardTitle>
                                    </CardHeader>
                                    <CardContent>
                                        <div className="max-h-[480px] overflow-y-auto text-sm text-slate-700 whitespace-pre-wrap leading-relaxed font-mono">
                                            {tailoredText}
                                        </div>
                                    </CardContent>
                                </Card>
                            )}
                        </div>

                        {/* Reset button */}
                        <div className="flex justify-center pt-2">
                            <Button
                                variant="outline"
                                onClick={handleReset}
                                className="px-8 text-sm"
                            >
                                Reset
                            </Button>
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
}
