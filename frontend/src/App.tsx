import { useState } from 'react'
import Page from './app/dashboard/page'

function App() {
  const [count, setCount] = useState(0)

  return (
    <>
    <Page/>
    </>
  )
}

export default App
