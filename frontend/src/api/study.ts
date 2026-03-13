import type { SkillGapItem } from '../types/api';

export interface StudyAnalyzeResponse {
  arisScore: number;
  vectorSimilarity: number;
  matchingSkills: SkillGapItem[];
  implicitlyDiscoveredSkills: string[];
  prerequisiteMetSkills: SkillGapItem[];
  bridgeableSkills: SkillGapItem[];
  hardGaps: SkillGapItem[];
  arisResume: string;
  chatGptResume: string;
  sessionResumeKey: string;
  sessionJobKey: string;
}

export async function analyzeStudy(
  resumeText: string,
  jobDescriptionText: string
): Promise<StudyAnalyzeResponse> {
  const res = await fetch('/api/study/analyze', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeText, jobDescriptionText }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Analysis failed. Please try again.');
  }
  return res.json();
}

export async function explainStudy(
  resumeText: string,
  jobDescriptionText: string,
  resumeKey?: string,
  jobKey?: string
): Promise<{ explanation: string }> {
  const res = await fetch('/api/study/explain', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeText, jobDescriptionText, resumeKey, jobKey }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to generate explanation.');
  }
  return res.json();
}

export interface StudyCompareResponse {
  ragResponse: string;
  graphRagResponse: string;
  tierSummary: {
    tier1Count: number;
    tier2Count: number;
    tier3Count: number;
    tier4Count: number;
    vectorSimilarity: number;
  };
}

export async function prepareResume(text: string): Promise<{ sessionKey: string }> {
  const res = await fetch('/api/study/prepare-resume', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to process resume.');
  }
  return res.json();
}

export async function prepareJob(text: string): Promise<{ sessionKey: string }> {
  const res = await fetch('/api/study/prepare-job', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to process job description.');
  }
  return res.json();
}

export async function generateComparison(
  resumeKey: string,
  jobKey: string,
  resumeText: string,
  jobDescriptionText: string
): Promise<StudyCompareResponse> {
  const res = await fetch('/api/study/generate-comparison', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeKey, jobKey, resumeText, jobDescriptionText }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to generate responses.');
  }
  return res.json();
}

export interface StudyMatchPreviewItem {
  jobTitle: string;
  companyName?: string;
  arisScore: number;
  vectorSimilarity: number;
  t1Count: number;
  t2Count: number;
  t3Count: number;
  t4Count: number;
  t5Count: number;
  topMatchingSkills: string[];
  topMissingSkills: string[];
}

export interface StudyMatchPreviewResponse {
  matches: StudyMatchPreviewItem[];
  totalJobsSearched: number;
}

export async function analyzeWithProfile(
  userId: string,
  jobDescriptionText: string
): Promise<StudyAnalyzeResponse> {
  const res = await fetch('/api/study/analyze-with-profile', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId, jobDescriptionText }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Analysis failed. Please try again.');
  }
  return res.json();
}

export async function getMatchPreview(
  resumeKey?: string,
  resumeText?: string
): Promise<StudyMatchPreviewResponse> {
  const res = await fetch('/api/study/match-preview', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeKey, resumeText }),
  });
  if (!res.ok) throw new Error('Failed to generate match preview.');
  return res.json();
}
