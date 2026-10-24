# Reading, navigation, and structured question editing

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Workspace-Editing-zh-CN.md)

## Find a question

Exams supports title/subject search, subject filtering, and date/title/subject/score-rate sorting. Question bank shows saved questions across exams; Mistakes derives its list from item grades. Use text search over question text, answers, notes, or exam names, subject/result filters, and sorting. More rows load as you scroll. [Meaning search](Features-Search.md) is a separate optional mode.

Open a question for a dedicated reading page. Previous/next follows the current list; Back restores the list position. The page shows the shared stem followed by every item in printed order, with labels inline. Each item keeps its own response, score, and grading notes. Select **Show answer and explanation** to reveal its standard answer, explanation, and solution steps.

**My notes → Save notes** stores personal review notes; recognition preserves them. **View in exam** opens the exam and highlights the question. Inside the exam, View original/Locate original opens its source; page navigation, zoom, fit, and rotation affect display.

The app remembers the normal window size and maximized state after closing. Long formulas wrap at suitable top-level operators, with indivisible groups fitting the reading width. Copying a formula retains the complete source.

## Add student work and confirm reviewed results

Use **Enter student answer** to enter work for each item without changing the standard answer. Save, then choose **Recognize again** in the exam to grade it. Entered responses are protected during recognition; changing one clears its old grade, teacher annotations, and error analysis and marks it uncertain until regraded. Blank exams switch to automatic detection when work is added. For photos or PDFs, use **More → Upload student answers** in the exam; see [import and limits](Features-Import.md).

Incorrect or partial work may show a separate **Error analysis** supported by the student's work. This supplements the verdict reason and standard solution; absent analysis does not imply a correct answer.

After checking transcription concerns against the original, select **Confirm result** in the **Needs review** notice. This clears the question's transcription review text while retaining content, personal notes, student work, and grades. It does not convert an uncertain grade to correct, so such a question can remain under Needs review. Finish active processing before saving responses or confirming results.

## Edit blocks and items

Select **Edit question**. The shared stem contains ordered text, LaTeX formula, table, and image blocks. Each item has a label, prompt, options, answer, explanation, points, and, on answered papers, student work and grading. Add or reorder items rather than building a nested tree; nested printed labels such as `(2)(a)` remain one combined label.

The current structure supports 1–100 items per question. With one item, the whole question normally belongs in the shared stem. Unknown points stay blank; totals are calculated from known item scores.

Source fields use percentages of the full unrotated page. Left 10%, top 20%, width 30%, height 10% means fractional storage `(0.1,0.2,0.3,0.1)`. These editor values differ from the model's integer bbox contract. Newly recognized questions usually locate the whole source page; existing precise regions survive re-recognition.

Image processing can provide a generated image while retaining source evidence. Changing a crop or handwriting flag does not erase handwriting; use an actually clean image for practice-sheet export.

## Save and verify

The editor holds a draft until validation succeeds. Structured saves mark attempt grades manual so later recognition preserves them. Non-grade text can still change during re-recognition, so back up before extensive reprocessing. Save notes and other pending edits before export or switching libraries.

A partial verdict requires an earned score strictly between zero and maximum. Unknown or uncertain scores stay blank. A failed save leaves the draft available for correction.

Keyboard: Ctrl+O imports, Ctrl+S saves quick edits/settings, Ctrl+E edits, Ctrl+0 fits the source page, and Ctrl+1–4 selects Exams/Question bank/Mistakes/Tasks. Pane dividers support dragging and keyboard arrows, Home, and End.

Sources: [bank](../../src/OhMyStudy.App/MainWindow.QuestionBank.cs), [reading page](../../src/OhMyStudy.App/MainWindow.QuestionPage.cs), [editor](../../src/OhMyStudy.App/MainWindow.Editing.cs), [student answers](../../src/OhMyStudy.App/MainWindow.StudentAnswers.cs), [confirmation](../../src/OhMyStudy.App/MainWindow.Confirmation.cs), [model](../../src/OhMyStudy.Domain/Models.cs).
