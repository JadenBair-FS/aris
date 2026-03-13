import { apiClient } from './client';

export const authApi = {
    deleteAccount: () => {
        return apiClient<{ message: string }>('/auth/account', { method: 'DELETE' });
    },
};
