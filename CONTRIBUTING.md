# Contributing to the Haiven Boss Room demo

Contributions to the Haiven integration in this repository are welcome.
Bug reports, fixes, and documentation improvements can start as a GitHub
issue or a pull request.

This repository's default branch is `main`. The upstream Boss Room guide
below still describes the original sample, including its code of conduct.
For changes here, follow this section instead of that guide's `develop`
branch and Unity CLA steps. Boss Room remains under the license in
`LICENSE.md`.

## Issues

Open a GitHub issue for a bug or a proposed change. Search open issues
first. Include the Unity editor version and the steps that show the
problem. Do not paste access tokens, passwords, or private endpoints.

## Pull requests

1. Fork the repository and branch from `main`.
2. Keep the change focused. Do not reformat unrelated sample files.
3. Use a [Conventional Commits](https://www.conventionalcommits.org/)
   subject:

   `type(scope): subject`

   `type` is one of `feat`, `fix`, `docs`, `deps`, `style`, `refactor`,
   `perf`, `test`, `build`, `ci`, `chore`, or `revert`. `scope` is
   optional.
4. Open the pull request against `main`. Run the checks below, say what
   changed, and say how you checked the change in the editor.

Do not commit access tokens, passwords, private endpoints, or local
editor credentials.

## Checks

From the repository root, run the editor pin check. On Linux and macOS
the interpreter is `python3`. On Windows, use `python`, the name a
normal Python install provides when `python3` is not on `PATH`.

Linux and macOS:

    python3 ci/test_editor_pin.py

Windows:

    python ci/test_editor_pin.py

The script checks that `ProjectSettings/ProjectVersion.txt` names Unity
editor `6000.0.52f1` and that the Haiven package `unity` fields stay on
`6000.0`. It is not a substitute for opening the project in that editor.

The Detect Project ID leak check reads
`ProjectSettings/ProjectSettings.asset`. It fails when `cloudProjectId`
has a value. From the repository root, run the same command as the
Actions job `check` (Detect Project ID leak). That Ubuntu job keeps
`python3`. On Windows, use `python` with the same arguments.

Linux and macOS:

    python3 -c "import re; import sys;content=open('ProjectSettings/ProjectSettings.asset').read();res = re.search(r'.*cloudProjectId:.*\w+\s*\n', content)!=None;status = 1 if res else 0; print('status '+str(status)); sys.exit(res)"

Windows:

    python -c "import re; import sys;content=open('ProjectSettings/ProjectSettings.asset').read();res = re.search(r'.*cloudProjectId:.*\w+\s*\n', content)!=None;status = 1 if res else 0; print('status '+str(status)); sys.exit(res)"

`status 0` means `cloudProjectId` is empty. Leave that field empty.

The upstream Boss Room guide follows.

# Contributing

Thank you for your interest in contributing to BossRoom!

Here are our guidelines for contributing:

* [Code of Conduct](#coc)
* [Ways to Contribute](#ways)
* [Issues and Bugs](#issue)
* [Feature Requests](#feature)
* [Improving Documentation](#docs)
* [Unity Contribution Agreement](#cla)
* [Pull Request Submission Guidelines](#submit-pr)

## <a name="coc"></a> Code of Conduct

Please help us keep BossRoom open and inclusive. Read and follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## <a name="ways"></a> Ways to Contribute

There are many ways in which you can contribute to the BossRoom.

### <a name="issue"></a> Issues and Bugs

If you find a bug in the source code, you can help us by submitting an issue to our
GitHub Repository. Even better, you can submit a Pull Request with a fix.

### <a name="feature"></a> Feature Requests

You can request a new feature by submitting an issue to our GitHub Repository.

If you would like to implement a new feature then consider what kind of change it is:

* **Major Changes** that you wish to contribute to the project should be discussed first with other developers. We will have a more formal process for this soon. For now submit your ideas as an issue.

* **Small Changes** can be directly submitted to the GitHub Repository
  as a Pull Request. See the section about [Pull Request Submission Guidelines](#submit-pr).

### <a name="docs"></a> Documentation

We accept changes and improvements to our documentation through the [Netcode for GameObjects Documentation repo](https://github.com/Unity-Technologies/com.unity.multiplayer.docs).

## <a name="cla"></a> Contributor License Agreements

When you open a pull request, you will be asked to enter into Unity's License Agreement which is based on The Apache Software Foundation's contribution agreement. We allow both individual contributions and contributions made on behalf of companies. We use an open source tool called CLA assistant. If you have any questions on our CLA, please submit an issue

## <a name="submit-pr"></a> Pull Request Submission Guidelines

We use the [Gitflow Workflow](https://www.atlassian.com/git/tutorials/comparing-workflows/gitflow-workflow) for the development of BossRoom. This means development happens on the **develop branch** and Pull Requests should be submited to it.

### Commit Message Guidelines
Always write a clear log message for your commits. One-line messages are fine for small changes, but bigger changes should look like this:

    $ git commit -m "A brief summary of the commit
    > 
    > A paragraph describing what changed and its impact."

### Line Endings Guidelines
The project is using Unix-style line endings.

Follow the instructions in the official [Git Configuration](https://git-scm.com/book/en/v2/Customizing-Git-Git-Configuration) documentation, the [Formatting and Whitespace](https://git-scm.com/book/en/v2/Customizing-Git-Git-Configuration#_formatting_and_whitespace) section, to enable `autocrlf` setting on your machine.
