# Thesis

Colin Percival, *Matching with Mismatches and Assorted Applications*, doctoral thesis,
University of Oxford, 2006: https://www.daemonology.net/papers/thesis.pdf

The thesis is not redistributed in this repository. To fetch it and produce the text dump
that `DESIGN.md` refers to by line number:

```sh
curl -sSL -o ref/thesis/thesis.pdf https://www.daemonology.net/papers/thesis.pdf
pdftotext -layout ref/thesis/thesis.pdf ref/thesis/thesis.txt
```

Chapter 1 is the matching-with-mismatches algorithm behind block alignment; Chapter 2 is the
delta compression method this project implements.
