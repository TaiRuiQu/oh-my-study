import type { ExecutionMode } from './api';

export const executionLabels: Record<ExecutionMode, string> = {
  request_permission: '请求权限',
  sandbox: '沙箱运行',
  bypass_permission: '绕过权限',
};
