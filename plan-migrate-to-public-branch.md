# Migrate to a Genuine GitHub Fork

## Objective

Move the existing `dev` work onto a branch in a genuine GitHub fork of
`zabszk/CSharp-SMTP-Server`, while preserving the existing repository as a
backup and establishing the author's `1.1.6` tag as the real Git ancestor.

The resulting branch will belong to the upstream repository's GitHub fork
network, so it can be viewed in the fork and used as the source of pull
requests to the author.

## Verified starting point

- Current repository: `https://github.com/krugertech/CSharp-SMTP-Server`
- Current working branch: `dev`
- Current `dev` tip when this plan was written: `e97aad9`
- Imported baseline commit: `2f7386e`
- Author's repository: `https://github.com/zabszk/CSharp-SMTP-Server`
- Author's `1.1.6` tag: `c23f9ad`
- Both baseline commits have the identical Git tree:
  `353fdb35c5735f19a366a93bc5751a69625e531e`
- `dev` contains 64 commits after the imported baseline.
- Those 64 commits form a linear history with no merge commits.

Because the two baseline trees are identical, no source ZIP replacement is
needed. Rebase can replace the unrelated imported parent with the genuine
upstream tag while replaying the existing changes.

## Safety rules

1. Do not delete, rename, force-push, or otherwise modify the existing GitHub
   repository until the migrated fork has been verified.
2. Perform the migration in a new sibling working directory, not in the
   current checkout.
3. Create the genuine fork under a different GitHub repository name, such as
   `CSharp-SMTP-Server-Fork`, to avoid a name collision and retain the current
   repository as a backup.
4. Do not push until the rebased branch has passed the content comparison and
   test suite.
5. Do not force-push the genuine fork's upstream-derived default branch.

## Phase 1: Create and clone the genuine fork

1. Open `https://github.com/zabszk/CSharp-SMTP-Server`.
2. Select **Fork**.
3. Choose the `krugertech` account as owner.
4. Give the fork a distinct name, for example
   `CSharp-SMTP-Server-Fork`.
5. Create the fork. Copying only the default branch is sufficient.
6. Clone the new fork into a new sibling directory:

```powershell
Set-Location C:\git\blue-source
git clone https://github.com/krugertech/CSharp-SMTP-Server-Fork.git CSharp-SMTP-Server-public
Set-Location CSharp-SMTP-Server-public
```

Expected remote roles:

- `origin`: the new genuine fork owned by `krugertech`
- `upstream`: the author's repository
- `legacy`: the existing standalone repository containing the work

Configure and fetch them:

```powershell
git remote add upstream https://github.com/zabszk/CSharp-SMTP-Server.git
git remote add legacy https://github.com/krugertech/CSharp-SMTP-Server.git
git fetch upstream --tags
git fetch legacy dev
git remote -v
```

## Phase 2: Reconnect the `dev` history

Create a temporary migration branch at the existing `dev` tip:

```powershell
git switch -c dev-clean legacy/dev
```

Replay every commit after the imported baseline onto the genuine upstream
`1.1.6` tag:

```powershell
git rebase --onto refs/tags/1.1.6 2f7386e dev-clean
```

Expected result:

- The old imported commit `2f7386e` is no longer an ancestor of `dev-clean`.
- The genuine upstream tag commit `c23f9ad` is an ancestor of `dev-clean`.
- The 64 migrated commits have new commit IDs because their ancestry changed.
- Commit messages and authorship remain intact.
- The final source tree remains identical to the old `legacy/dev` tree.

Conflicts are not expected because the imported baseline and upstream tag have
identical trees. If a conflict nevertheless occurs, stop and inspect it rather
than guessing or skipping a commit. The existing repository remains the
authoritative backup.

## Phase 3: Verify before pushing

Confirm ancestry:

```powershell
git merge-base --is-ancestor refs/tags/1.1.6 dev-clean
if ($LASTEXITCODE -ne 0) { throw "Upstream 1.1.6 is not an ancestor" }

git merge-base --is-ancestor 2f7386e dev-clean
if ($LASTEXITCODE -eq 0) { throw "Imported root is still an ancestor" }
```

Confirm that migration did not change the final contents:

```powershell
git diff --exit-code legacy/dev dev-clean
if ($LASTEXITCODE -ne 0) { throw "Migrated content differs from legacy/dev" }
```

Inspect the rewritten history:

```powershell
git log --oneline --decorate --graph --max-count=80 dev-clean
git rev-list --count refs/tags/1.1.6..dev-clean
```

Run the repository's normal build and tests:

```powershell
dotnet test
```

All checks must pass before publishing the branch.

## Phase 4: Publish the migrated branch

Push `dev-clean` as `dev` to the genuine fork:

```powershell
git push --set-upstream origin dev-clean:dev
```

Verify on GitHub that:

1. The new repository is labelled as a fork of
   `zabszk/CSharp-SMTP-Server`.
2. The `dev` branch exists in the new fork.
3. GitHub can compare the fork's `dev` branch with the author's repository.
4. The existing standalone repository remains unchanged and accessible.

## Pull-request considerations

The author's `master` branch currently contains commits after the `1.1.6`
release. The migrated `dev` branch will have correct ancestry through the
`1.1.6` tag, but that does not automatically make the entire branch an ideal
pull request against the current upstream `master`.

The fork also contains project-specific rebranding, packaging, documentation,
and larger behavioral changes. Do not open one pull request containing all of
`dev` unless the author explicitly requests it. For upstream contributions,
prefer a separate topic branch created from the current `upstream/master`, then
cherry-pick or reimplement only the focused commits intended for that pull
request:

```powershell
git fetch upstream
git switch -c upstream-topic upstream/master
git cherry-pick <selected-migrated-commit>
```

Resolve and test each topic branch independently before opening its pull
request.

## Cleanup and final naming

Only after the new fork and branch have been verified should repository naming
or archival be reconsidered. Safe options include:

- Keep the old repository as an explicitly labelled legacy backup.
- Archive the old repository on GitHub.
- Rename the old repository and later rename the genuine fork.

Deleting the old repository is unnecessary for the migration and is outside
the scope of this plan.

