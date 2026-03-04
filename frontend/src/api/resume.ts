import { apiClient, fetchBlobWithAuth } from './client';
import type { UserProfileDetail, TailoredBullet, MatchAnalysisResult } from '../types/api';

function buildTierPayload(analysis: MatchAnalysisResult) {
    return {
        matchingSkills: analysis.matchingSkills.map(s => s.skillName),
        implicitSkills: analysis.implicitlyDiscoveredSkills,
        prereqMetSkills: analysis.prerequisiteMetSkills.map(s => s.skillName),
        bridgeableSkills: analysis.bridgeableSkills.map(s => s.skillName),
        hardGaps: analysis.hardGaps.map(s => s.skillName),
    };
}

export const resumeApi = {
    uploadPdf: (file: File) => {
        const formData = new FormData();
        formData.append('file', file);
        return apiClient<{ message: string; id: string }>('/resume/upload', {
            method: 'POST',
            body: formData,
        });
    },
    uploadText: (content: string) => {
        return apiClient<{ message: string; id: string }>('/resume/upload-text', {
            method: 'POST',
            body: JSON.stringify({ content }),
        });
    },
    getProfileByUserId: (clerkId: string) => {
        return apiClient<UserProfileDetail>(`/resume/by-user/${clerkId}`);
    },
    getProfile: (id: string) => {
        return apiClient<UserProfileDetail>(`/resume/${id}`);
    },
    tailor: (userProfileId: string, jobId: string, analysis?: MatchAnalysisResult) => {
        const tierPayload = analysis ? buildTierPayload(analysis) : {};
        return apiClient<TailoredBullet[]>('/resume/tailor', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId, ...tierPayload }),
        });
    },
    tailorPdf: async (userProfileId: string, jobId: string, analysis?: MatchAnalysisResult): Promise<Blob> => {
        const tierPayload = analysis ? buildTierPayload(analysis) : {};

        // Uses fetchBlobWithAuth so the Clerk JWT is injected — avoids 401
        const response = await fetchBlobWithAuth('/resume/tailor-pdf', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId, ...tierPayload }),
        });

        if (!response.ok) {
            const text = await response.text();
            throw new Error(text || `PDF generation failed with status ${response.status}`);
        }

        return response.blob();
    },
};
