import type { Personnel } from '../types'

interface Props {
  personnel: Personnel
  selected: boolean
  onClick: () => void
}

function initials(name: string) {
  return name.split(' ').map(w => w[0]).slice(0, 2).join('').toUpperCase()
}

export default function PersonnelCard({ personnel, selected, onClick }: Props) {
  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-3 py-2.5 rounded-xl transition-all border ${
        selected
          ? 'bg-brand-50 border-brand-200 shadow-sm'
          : 'bg-white border-gray-200 hover:bg-gray-50 hover:border-gray-300'
      }`}
    >
      <div className="flex items-center gap-2.5">
        <div className={`w-9 h-9 rounded-full flex-shrink-0 flex items-center justify-center text-xs font-semibold ${
          selected ? 'bg-brand-600 text-white' : 'bg-gray-100 text-gray-600'
        }`}>
          {initials(personnel.displayName)}
        </div>
        <div className="min-w-0">
          <p className={`text-sm font-medium truncate ${selected ? 'text-brand-700' : 'text-gray-900'}`}>
            {personnel.displayName}
          </p>
          <p className="text-xs text-gray-500 truncate">
            {[personnel.title, personnel.department].filter(Boolean).join(' · ')}
          </p>
        </div>
      </div>
    </button>
  )
}
