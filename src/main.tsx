import React from 'react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import ReactDOM from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HashRouter } from 'react-router-dom';
import { App } from './app/App';
import './styles.css';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: false, staleTime: 10_000, refetchOnWindowFocus: true },
    mutations: { retry: false },
  },
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <FluentProvider theme={webLightTheme} className="fluent-root">
      <QueryClientProvider client={queryClient}>
        <HashRouter>
          <App />
        </HashRouter>
      </QueryClientProvider>
    </FluentProvider>
  </React.StrictMode>,
);
