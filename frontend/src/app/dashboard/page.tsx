import { redirect } from 'next/navigation'

/** The Dashboard was folded into Analytics (the home page); old links and
 *  bookmarks land here and move on. */
export default function DashboardRedirect() {
  redirect('/analytics')
}
