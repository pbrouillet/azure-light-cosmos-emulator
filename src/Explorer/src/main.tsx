import { StrictMode, useEffect, useMemo, useState } from 'react'
import { createRoot } from 'react-dom/client'
import {
  FluentProvider,
  makeStyles,
  webDarkTheme,
  webLightTheme,
} from '@fluentui/react-components'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import './index.css'
import App from './App'
import { ThemeContext } from './theme'

const useStyles = makeStyles({
  provider: {
    height: '100%',
  },
})

export function Root() {
  const styles = useStyles()
  const [isDark, setIsDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  const themeValue = useMemo(
    () => ({ isDark, toggle: () => setIsDark((current) => !current) }),
    [isDark],
  )

  useEffect(() => {
    document.documentElement.style.colorScheme = isDark ? 'dark' : 'light'
  }, [isDark])

  return (
    <ThemeContext.Provider value={themeValue}>
      <FluentProvider className={styles.provider} theme={isDark ? webDarkTheme : webLightTheme}>
        <App />
      </FluentProvider>
    </ThemeContext.Provider>
  )
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter basename="/explorer">
        <Root />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
