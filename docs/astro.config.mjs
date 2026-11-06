// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://fuseraft.ai',
	base: '/fuseraft-web',
	// Redirect targets don't get the base prepended; sources do.
	redirects: { '/guides/repl': '/fuseraft-web/guides/chat/' },
	integrations: [
		starlight({
			title: 'fuseraft-web',
			description: 'fuseraft in the browser, shared by a team: sessions, chats, approvals and remote daemons, with an account per person.',
			logo: { src: './src/assets/logo.svg' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/fuseraft/fuseraft-web' }],
			editLink: { baseUrl: 'https://github.com/fuseraft/fuseraft-web/edit/main/docs/' },
			lastUpdated: true,
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{
					label: 'Start here',
					items: ['start/install', 'start/quickstart'],
				},
				{
					label: 'Guides',
					items: [
						'guides/accounts',
						'guides/single-sign-on',
						'guides/sessions',
						'guides/chat',
						'guides/approvals',
						'guides/daemons',
						'guides/more',
					],
				},
				{
					label: 'Deploying',
					items: ['deploy/service', 'deploy/reverse-proxy', 'deploy/restarts', 'deploy/backups'],
				},
				{
					label: 'Reference',
					items: ['reference/config', 'reference/cli'],
				},
				{ label: 'How fuseraft-web works', link: '/design/' },
			],
		}),
	],
});
