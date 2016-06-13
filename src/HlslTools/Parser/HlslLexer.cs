﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using HlslTools.Diagnostics;
using HlslTools.Syntax;
using HlslTools.Text;

namespace HlslTools.Parser
{
    public sealed partial class HlslLexer : ILexer
    {
        private readonly IIncludeFileSystem _fileSystem;
        private readonly List<SyntaxNode> _leadingTrivia = new List<SyntaxNode>();
        private readonly List<SyntaxNode> _trailingTrivia = new List<SyntaxNode>();
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();

        private LexerMode _mode;

        public bool ExpandMacros { get; set; }
        public bool ProcessIncludeDirectives { get; set; } = true;

        private List<SyntaxToken> _expandedMacroTokens;
        private int _expandedMacroIndex;

        // This is the index used in TextSpans - it contains the offset
        // from the beginning of the first text buffer to the start of the current file.
        private int _currentFileSegmentAbsolutePosition;

        private DirectiveStack _directives;
        private SyntaxKind _kind;
        private SyntaxKind _contextualKind;
        private object _value;
        private int _start;

        private readonly Stack<IncludeContext> _includeStack;
        private CharReader _charReader;

        private class IncludeContext
        {
            public readonly CharReader CharReader;
            public readonly SourceText Text;

            public IncludeContext(SourceText text)
            {
                CharReader = new CharReader(text);
                Text = text;
            }
        }

        // Stores something like this:
        // - {main.hlsl, 0, 100}
        // - {included.hlsli, 0, 10}
        // - {main.hlsl, 101, 200}
        internal List<FileSegment> FileSegments { get; }

        public HlslLexer(SourceText text, ParserOptions options = null, IIncludeFileSystem fileSystem = null, int start = 0)
        {
            _fileSystem = fileSystem ?? new DummyFileSystem();
            _directives = DirectiveStack.Empty;

            if (options != null)
                foreach (var define in options.PreprocessorDefines)
                {
                    _directives = _directives.Add(new Directive(new ObjectLikeDefineDirectiveTriviaSyntax(
                        null, null, SyntaxFactory.ParseToken(define), new List<SyntaxToken>(), null, true)));
                }

            ExpandMacros = true;

            FileSegments = new List<FileSegment>();
            _includeStack = new Stack<IncludeContext>();
            PushIncludeContext(text, start);
        }

        // TODO: This won't be necessary once we're using separate lexer for CGPROGRAMs.
        void ILexer.ResetDirectiveStack()
        {
            _directives = DirectiveStack.Empty;
        }

        // TODO: This shouldn't be here.
        void ILexer.AddPreprocessorInclude(string path)
        {
            // TODO
        }

        public SourceText Text => _includeStack.Peek().Text;

        public SyntaxToken Lex(LexerMode mode)
        {
            // First check if we're in the middle of expanding a macro reference token.
            if (_expandedMacroTokens != null)
            {
                var result = _expandedMacroTokens[_expandedMacroIndex++];
                if (_expandedMacroIndex == _expandedMacroTokens.Count)
                    _expandedMacroTokens = null;
                return result;
            }

            _mode = mode;

            SyntaxToken token;
            switch (_mode)
            {
                case LexerMode.Directive:
                    token = LexDirectiveToken();
                    break;
                default:
                    token = LexSyntaxToken();
                    break;
            }

            // Swallow end-of-file tokens from include files.
            if (token.Kind == SyntaxKind.EndOfFileToken && _includeStack.Count > 1)
            {
                var originalToken = token;

                PopIncludeContext();
                token = Lex(mode);
                token = token.WithLeadingTrivia(originalToken.LeadingTrivia.AddRange(token.LeadingTrivia));

                // this is a bit weird, but we need to also update the leading trivia on the macro reference,
                // because that's what we use when outputting code.
                if (token.MacroReference != null)
                    token = token.WithOriginalMacroReference(token.MacroReference.WithLeadingTrivia(token.LeadingTrivia), token.IsFirstTokenInMacroExpansion);
            }

            // Expand macros and attach as a special kind of trivia.
            if (token.Kind == SyntaxKind.IdentifierToken && ExpandMacros)
            {
                List<SyntaxToken> expandedTokens;
                MacroReference macroReference;
                if (TryExpandMacro(token, new BaseMacroExpansionLexer(this), out expandedTokens, out macroReference))
                {
                    if (expandedTokens.Count == 0) // Can happen for macros with empty body.
                    {
                        // Attach macro call as leading trivia on next token.
                        token = Lex(mode);

                        // If next token is itself a macro reference, attach macro call to original token.
                        var tokenToAttachMacroCallTo = token;
                        if (token.MacroReference != null)
                            tokenToAttachMacroCallTo = token.MacroReference.NameToken;

                        var leadingTrivia = new List<SyntaxNode>();
                        leadingTrivia.Add(new EmptyExpandedMacroTrivia(macroReference));
                        leadingTrivia.AddRange(tokenToAttachMacroCallTo.LeadingTrivia);

                        token = token.WithLeadingTrivia(leadingTrivia.ToImmutableArray());

                        if (token.MacroReference != null)
                            token = token.WithOriginalMacroReference(
                                token.MacroReference.WithLeadingTrivia(leadingTrivia.ToImmutableArray()),
                                token.IsFirstTokenInMacroExpansion);
                    }
                    else
                    {
                        if (expandedTokens.Count > 1)
                        {
                            _expandedMacroTokens = expandedTokens;
                            _expandedMacroIndex = 1;
                        }
                        token = expandedTokens[0];
                    }
                }
            }

            return token;
        }

        private void NextChar()
        {
            _charReader.NextChar();
            FileSegments.Last().Length++;
        }

        private SyntaxToken LexSyntaxToken()
        {
            // Keep reading leading trivia until there is no more trivia.
            // This is because we might be moving to new include files, each with leading trivia.
            ImmutableArray<SyntaxNode> leadingTrivia = ImmutableArray<SyntaxNode>.Empty;
            _diagnostics.Clear();
            while (true)
            {
                _leadingTrivia.Clear();                
                _start = _charReader.Position;
                ReadTrivia(_leadingTrivia, isTrailing: false);
                var newLeadingTrivia = _leadingTrivia.ToImmutableArray();
                if (newLeadingTrivia.IsEmpty)
                    break;
                leadingTrivia = leadingTrivia.AddRange(newLeadingTrivia);
            }

            _kind = SyntaxKind.BadToken;
            _contextualKind = SyntaxKind.None;
            _value = null;
            _diagnostics.Clear();
            _start = _charReader.Position;
            ReadToken();
            var end = _charReader.Position;
            var kind = _kind;
            var span = TextSpan.FromBounds(Text, _start, end);
            var text = Text.GetText(span);
            var diagnostics = _diagnostics.ToImmutableArray();

            _trailingTrivia.Clear();
            _diagnostics.Clear();
            _start = _charReader.Position;
            ReadTrivia(_trailingTrivia, isTrailing: true);
            var trailingTrivia = _trailingTrivia.ToImmutableArray();

            return new SyntaxToken(kind, _contextualKind, false, MakeAbsolute(span), span, text, _value, leadingTrivia, trailingTrivia, diagnostics, null, false);
        }

        private SourceRange MakeAbsolute(TextSpan span)
        {
            return new SourceRange(new SourceLocation(_currentFileSegmentAbsolutePosition + span.Start - FileSegments.Last().Start), span.Length);
        }

        private TextSpan CurrentSpan => TextSpan.FromBounds(Text, _start, _charReader.Position);

        private TextSpan CurrentSpanStart => TextSpan.FromBounds(Text, _start, Math.Min(_start + 2, Text.Length));

        private void ReadTrivia(List<SyntaxNode> target, bool isTrailing)
        {
            var onlyWhitespaceOnLine = !isTrailing;

            while (true)
            {
                switch (_charReader.Current)
                {
                    case '\n':
                    case '\r':
                        {
                            ReadEndOfLine();
                            AddTrivia(target, SyntaxKind.EndOfLineTrivia);
                            if (isTrailing)
                                return;
                            onlyWhitespaceOnLine = true;
                        }
                        break;
                    case '/':
                        if (_charReader.Peek() == '/')
                        {
                            ReadSinglelineComment();
                            AddTrivia(target, SyntaxKind.SingleLineCommentTrivia);
                            onlyWhitespaceOnLine = false;
                        }
                        else if (_charReader.Peek() == '*')
                        {
                            ReadMultilineComment();
                            AddTrivia(target, SyntaxKind.MultiLineCommentTrivia);
                            onlyWhitespaceOnLine = false;
                        }
                        else
                        {
                            return;
                        }
                        break;
                    case '#':
                        var shouldContinue = LexDirectiveAndExcludedTrivia(isTrailing || !onlyWhitespaceOnLine, target);
                        if (!shouldContinue)
                            return;
                        break;
                    case '\\':
                        if (_charReader.Peek() != '\r' && _charReader.Peek() != '\n')
                            goto default;
                        _kind = SyntaxKind.BackslashNewlineTrivia;
                        NextChar();
                        ReadEndOfLine();
                        AddTrivia(target, SyntaxKind.BackslashNewlineTrivia);
                        break;

                    default:
                        if (char.IsWhiteSpace(_charReader.Current))
                        {
                            ReadWhitespace();
                            AddTrivia(target, SyntaxKind.WhitespaceTrivia);
                        }
                        else
                        {
                            return;
                        }
                        break;
                }
            }
        }

        private bool LexDirectiveAndExcludedTrivia(
            bool afterNonWhitespaceOnLine,
            List<SyntaxNode> triviaList)
        {
            var directive = LexSingleDirective(true, true, afterNonWhitespaceOnLine, triviaList);

            if (directive.Kind == SyntaxKind.IncludeDirectiveTrivia && ProcessIncludeDirectives)
            {
                var includeDirective = (IncludeDirectiveTriviaSyntax)directive;
                var includeFilename = includeDirective.TrimmedFilename;

                SourceText include;
                try
                {
                    include = _fileSystem.GetInclude(includeFilename);
                    if (include == null)
                    {
                        includeDirective = includeDirective.WithDiagnostic(Diagnostic.Format(includeDirective.GetTextSpan(), DiagnosticId.IncludeNotFound, includeFilename));
                        triviaList.Add(includeDirective);
                    }
                }
                catch (Exception ex)
                {
                    includeDirective = includeDirective.WithDiagnostic(new Diagnostic(includeDirective.GetTextSpan(), DiagnosticId.IncludeNotFound, ex.Message));
                    include = null;
                    triviaList.Add(includeDirective);
                }

                if (include != null)
                {
                    triviaList.Add(includeDirective);
                    PushIncludeContext(include);
                    return false;
                }
            }

            // also lex excluded stuff
            var branching = directive as BranchingDirectiveTriviaSyntax;
            if (branching != null && !branching.BranchTaken)
                LexExcludedDirectivesAndTrivia(true, triviaList);

            return true;
        }

        private void PushIncludeContext(SourceText text, int start = 0)
        {
            _currentFileSegmentAbsolutePosition = FileSegments.Sum(x => x.Length);

            var includeContext = new IncludeContext(text);
            _includeStack.Push(includeContext);
            _charReader = includeContext.CharReader;
            _charReader.Reset(start);
            FileSegments.Add(new FileSegment(text, _charReader.Position));
        }

        private void PopIncludeContext()
        {
            _currentFileSegmentAbsolutePosition = FileSegments.Sum(x => x.Length);

            _includeStack.Pop();
            _charReader = _includeStack.Peek().CharReader;

            FileSegments.Add(new FileSegment(_includeStack.Peek().Text, _charReader.Position));
        }

        private void LexExcludedDirectivesAndTrivia(bool endIsActive, List<SyntaxNode> triviaList)
        {
            while (true)
            {
                bool hasFollowingDirective;
                var text = LexDisabledText(out hasFollowingDirective);
                if (text != null)
                    triviaList.Add(text);

                if (!hasFollowingDirective)
                    break;

                var directive = LexSingleDirective(false, endIsActive, false, triviaList);
                var branching = directive as BranchingDirectiveTriviaSyntax;
                if (directive.Kind == SyntaxKind.EndIfDirectiveTrivia || (branching != null && branching.BranchTaken))
                    break;

                if (directive.Kind.IsIfLikeDirective())
                    LexExcludedDirectivesAndTrivia(false, triviaList);
            }
        }

        // consume text up to the next directive
        private SyntaxNode LexDisabledText(out bool followedByDirective)
        {
            _start = _charReader.Position;

            int lastLineStart = _charReader.Position;
            bool allWhitespace = true;

            while (true)
            {
                char ch = _charReader.Current;
                switch (ch)
                {
                    case '\0':
                        followedByDirective = false;
                        return _charReader.Position - _start > 0 ? CreateDisabledText() : null;
                    case '#':
                        followedByDirective = true;
                        if (lastLineStart < _charReader.Position && !allWhitespace)
                            goto default;

                        _charReader.Reset(lastLineStart); // reset so directive parser can consume the starting whitespace on this line
                        return _charReader.Position - _start > 0 ? CreateDisabledText() : null;
                    case '\r':
                    case '\n':
                        ReadEndOfLine();
                        lastLineStart = _charReader.Position;
                        allWhitespace = true;
                        break;
                    default:
                        allWhitespace = allWhitespace && char.IsWhiteSpace(ch);
                        NextChar();
                        break;
                }
            }
        }

        private SyntaxTrivia CreateDisabledText()
        {
            var end = _charReader.Position;
            var span = TextSpan.FromBounds(Text, _start, end);
            var text = Text.GetText(span);
            return new SyntaxTrivia(SyntaxKind.DisabledTextTrivia, text, MakeAbsolute(span), span, ImmutableArray<Diagnostic>.Empty);
        }

        private SyntaxNode LexSingleDirective(
            bool isActive,
            bool endIsActive,
            bool afterNonWhitespaceOnLine,
            List<SyntaxNode> triviaList)
        {
            _start = _charReader.Position;

            if (char.IsWhiteSpace(_charReader.Current))
            {
                ReadWhitespace();
                AddTrivia(triviaList, SyntaxKind.WhitespaceTrivia);
            }

            var saveMode = _mode;
            var saveExpandMacros = ExpandMacros;

            _mode = LexerMode.Directive;
            ExpandMacros = false;

            var dp = new DirectiveParser(this, _directives);
            var directive = dp.ParseDirective(isActive, endIsActive, afterNonWhitespaceOnLine);

            if (!isActive || directive.Kind != SyntaxKind.IncludeDirectiveTrivia)
                triviaList.Add(directive);

            _directives = directive.ApplyDirectives(_directives);
            ExpandMacros = saveExpandMacros;
            _mode = saveMode;

            // Directive parser sometimes leaves charReader at start of token *after* the one we want.
            _charReader.Reset(directive.GetLastToken().GetLastSpanIncludingTrivia().End);
            _start = _charReader.Position;

            return directive;
        }

        private void ReadEndOfLine()
        {
            if (_charReader.Current == '\r')
            {
                NextChar();

                if (_charReader.Current == '\n')
                    NextChar();
            }
            else
            {
                NextChar();
            }
        }

        private void ReadSinglelineComment()
        {
            _kind = SyntaxKind.SingleLineCommentTrivia;
            while (true)
            {
                switch (_charReader.Current)
                {
                    case '\0':
                        return;

                    case '\r':
                    case '\n':
                        return;

                    default:
                        NextChar();
                        break;
                }
            }
        }

        private void ReadMultilineComment()
        {
            NextChar(); // Skip /
            NextChar(); // Skip *

            _kind = SyntaxKind.MultiLineCommentTrivia;

            while (true)
            {
                switch (_charReader.Current)
                {
                    case '\0':
                        _diagnostics.ReportUnterminatedComment(CurrentSpanStart);
                        return;

                    case '*':
                        NextChar();
                        if (_charReader.Current == '/')
                        {
                            NextChar();
                            return;
                        }
                        break;

                    default:
                        NextChar();
                        break;
                }
            }
        }

        private void ReadWhitespace()
        {
            while (char.IsWhiteSpace(_charReader.Current) &&
                   _charReader.Current != '\r' &&
                   _charReader.Current != '\n')
            {
                NextChar();
            }
        }

        private void AddTrivia(List<SyntaxNode> target, SyntaxKind kind)
        {
            var start = _start;
            var end = _charReader.Position;
            var span = TextSpan.FromBounds(Text, start, end);
            var text = Text.GetText(span);
            var diagnostics = _diagnostics.ToImmutableArray();
            var trivia = new SyntaxTrivia(kind, text, MakeAbsolute(span), span, diagnostics);
            target.Add(trivia);

            _diagnostics.Clear();
            _start = _charReader.Position;
        }

        private void ReadToken()
        {
            switch (_charReader.Current)
            {
                case '\0':
                    if (_includeStack.Count == 1 && _directives.HasUnfinishedIf())
                        _diagnostics.Add(Diagnostic.Format(CurrentSpanStart, DiagnosticId.EndIfDirectiveExpected));
                    _kind = SyntaxKind.EndOfFileToken;
                    break;

                case '~':
                    _kind = SyntaxKind.TildeToken;
                    NextChar();
                    break;

                case '&':
                    NextChar();
                    if (_charReader.Current == '&')
                    {
                        _kind = SyntaxKind.AmpersandAmpersandToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.AmpersandEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.AmpersandToken;
                    }
                    break;

                case '|':
                    NextChar();
                    if (_charReader.Current == '|')
                    {
                        _kind = SyntaxKind.BarBarToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.BarEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.BarToken;
                    }
                    break;

                case '^':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.CaretEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.CaretToken;
                    }
                    break;

                case '?':
                    _kind = SyntaxKind.QuestionToken;
                    NextChar();
                    break;

                case '(':
                    _kind = SyntaxKind.OpenParenToken;
                    NextChar();
                    break;

                case ')':
                    _kind = SyntaxKind.CloseParenToken;
                    NextChar();
                    break;

                case '[':
                    _kind = SyntaxKind.OpenBracketToken;
                    NextChar();
                    break;

                case ']':
                    _kind = SyntaxKind.CloseBracketToken;
                    NextChar();
                    break;

                case '{':
                    _kind = SyntaxKind.OpenBraceToken;
                    NextChar();
                    break;

                case '}':
                    _kind = SyntaxKind.CloseBraceToken;
                    NextChar();
                    break;

                case '.':
                    if (Char.IsDigit(_charReader.Peek()))
                        ReadNumber();
                    else
                    {
                        _kind = SyntaxKind.DotToken;
                        NextChar();
                    }
                    break;

                case '+':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.PlusEqualsToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '+')
                    {
                        _kind = SyntaxKind.PlusPlusToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.PlusToken;
                    }
                    break;

                case '-':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.MinusEqualsToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '-')
                    {
                        _kind = SyntaxKind.MinusMinusToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.MinusToken;
                    }
                    break;

                case '*':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.AsteriskEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.AsteriskToken;
                    }
                    break;

                case '/':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.SlashEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.SlashToken;
                    }
                    break;

                case '%':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.PercentEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.PercentToken;
                    }
                    break;

                case ',':
                    _kind = SyntaxKind.CommaToken;
                    NextChar();
                    break;

                case ';':
                    _kind = SyntaxKind.SemiToken;
                    NextChar();
                    break;

                case ':':
                    NextChar();
                    if (_charReader.Current == ':')
                    {
                        _kind = SyntaxKind.ColonColonToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.ColonToken;
                    }
                    break;

                case '=':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.EqualsEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.EqualsToken;
                    }
                    break;

                case '!':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.ExclamationEqualsToken;
                        NextChar();
                    }
                    else
                    {
                        _kind = SyntaxKind.NotToken;
                    }
                    break;

                case '<':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.LessThanEqualsToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '<')
                    {
                        NextChar();
                        if (_charReader.Current == '=')
                        {
                            _kind = SyntaxKind.LessThanLessThanEqualsToken;
                            NextChar();
                        }
                        else
                        {
                            _kind = SyntaxKind.LessThanLessThanToken;
                        }
                    }
                    else
                    {
                        _kind = SyntaxKind.LessThanToken;
                    }
                    break;

                case '>':
                    NextChar();
                    if (_charReader.Current == '=')
                    {
                        _kind = SyntaxKind.GreaterThanEqualsToken;
                        NextChar();
                    }
                    else if (_charReader.Current == '>')
                    {
                        NextChar();
                        if (_charReader.Current == '=')
                        {
                            _kind = SyntaxKind.GreaterThanGreaterThanEqualsToken;
                            NextChar();
                        }
                        else
                        {
                            _kind = SyntaxKind.GreaterThanGreaterThanToken;
                        }
                    }
                    else
                    {
                        _kind = SyntaxKind.GreaterThanToken;
                    }
                    break;

                case '"':
                    ReadString();
                    break;

                default:
                    if (char.IsLetter(_charReader.Current) || _charReader.Current == '_' || (_mode == LexerMode.UnitySyntax && char.IsDigit(_charReader.Current) && _charReader.Peek() == 'D'))
                        ReadIdentifierOrKeyword();
                    else if (char.IsDigit(_charReader.Current))
                        ReadNumber();
                    else
                        ReadInvalidCharacter();

                    break;
            }
        }

        private void ReadInvalidCharacter()
        {
            var c = _charReader.Current;
            NextChar();
            _diagnostics.ReportIllegalInputCharacter(CurrentSpan, c);
        }

        private void ReadString()
        {
            _kind = SyntaxKind.StringLiteralToken;

            // Skip first double quote
            NextChar();

            var sb = new StringBuilder();

            while (true)
            {
                switch (_charReader.Current)
                {
                    case '\0':
                        _diagnostics.ReportUnterminatedString(CurrentSpanStart);
                        goto ExitLoop;

                    case '\\':
                        sb.Append(_charReader.Current);
                        NextChar();
                        sb.Append(_charReader.Current);
                        NextChar();
                        break;

                    case '"':
                        NextChar();

                        if (_charReader.Current != '"')
                            goto ExitLoop;

                        sb.Append(_charReader.Current);
                        NextChar();
                        break;

                    default:
                        sb.Append(_charReader.Current);
                        NextChar();
                        break;
                }
            }

            ExitLoop:
            _value = sb.ToString();
        }

        private void ReadNumber()
        {
            // Just read everything that looks like it could be a number -- we will
            // verify it afterwards by proper number parsing.

            var sb = new StringBuilder();
            var hasExponentialModifier = false;
            var hasDotModifier = false;
            var hasFloatSuffix = false;
            var hasHexModifier = false;
            var isPreprocessingNumber = false;

            while (true)
            {
                switch (_charReader.Current)
                {
                    // dot
                    case '.':
                        if (hasHexModifier)
                            goto ExitLoop;
                        sb.Append(_charReader.Current);
                        NextChar();
                        hasDotModifier = true;
                        break;

                    // special handling for e, it could be the exponent indicator
                    // followed by an optional sign

                    case 'E':
                    case 'e':
                        if (hasHexModifier)
                            goto case '0';

                        sb.Append(_charReader.Current);
                        NextChar();
                        hasExponentialModifier = true;
                        if (_charReader.Current == '-' || _charReader.Current == '+')
                        {
                            sb.Append(_charReader.Current);
                            NextChar();
                        }
                        break;

                    case 'F':
                    case 'f':
                    case 'H':
                    case 'h':
                        if (hasHexModifier)
                            goto case '0';
                        hasFloatSuffix = true;
                        NextChar();
                        goto ExitLoop;

                    case 'X':
                    case 'x':
                        hasHexModifier = true;
                        sb.Append(_charReader.Current);
                        NextChar();
                        break;

                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        sb.Append(_charReader.Current);
                        NextChar();
                        break;

                    case 'A':
                    case 'B':
                    case 'C':
                    case 'D':
                    case 'a':
                    case 'b':
                    case 'c':
                    case 'd':
                        if (hasHexModifier)
                            goto case '0';
                        goto default;

                    default:
                        if (_mode == LexerMode.Directive && char.IsLetter(_charReader.Current))
                        {
                            isPreprocessingNumber = true;
                            goto case '0';
                        }
                        goto ExitLoop;
                }
            }

            ExitLoop:

            var text = sb.ToString();

            if (isPreprocessingNumber)
                _value = ReadPreprocessingNumber(text);
            else if ((hasDotModifier || hasExponentialModifier || hasFloatSuffix) && !hasHexModifier)
                _value = ReadDouble(text);
            else
                _value = ReadInt32OrInt64(text, hasHexModifier);
        }

        private string ReadPreprocessingNumber(string text)
        {
            _kind = SyntaxKind.PreprocessingNumber;
            return text;
        }

        private double ReadDouble(string text)
        {
            _kind = SyntaxKind.FloatLiteralToken;

            try
            {
                return double.Parse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                _diagnostics.ReportNumberTooLarge(CurrentSpan, text);
            }
            catch (FormatException)
            {
                _diagnostics.ReportInvalidReal(CurrentSpan, text);
            }
            return 0.0;
        }

        private object ReadInt32OrInt64(string text, bool hasHexModifier)
        {
            _kind = SyntaxKind.IntegerLiteralToken;

            var int64 = ReadInt64(text, hasHexModifier);

            // If the integer can be represented as Int32 we return
            // an Int32 literal. Otherwise we return an Int64.

            if (int64 >= int.MinValue && int64 <= int.MaxValue)
                return (int) int64;

            return int64;
        }

        private long ReadInt64(string text, bool hasHexModifier)
        {
            if (hasHexModifier)
            {
                try
                {
                    return Convert.ToInt64(text, 16);
                }
                catch (OverflowException)
                {
                    _diagnostics.ReportNumberTooLarge(CurrentSpan, text);
                }
                catch (FormatException)
                {
                    _diagnostics.ReportInvalidHex(CurrentSpan, text);
                }

                return 0;
            }

            try
            {
                return long.Parse(text, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                _diagnostics.ReportNumberTooLarge(CurrentSpan, text);
            }
            catch (FormatException)
            {
                _diagnostics.ReportInvalidInteger(CurrentSpan, text);
            }

            return 0;
        }

        private long ReadOctalValue(string octal)
        {
            long val = 0;

            for (int i = octal.Length - 1, j = 0; i >= 0; i--, j++)
            {
                int c;

                try
                {
                    c = int.Parse(new string(octal[i], 1), CultureInfo.InvariantCulture);

                    if (c > 7)
                    {
                        _diagnostics.ReportInvalidOctal(CurrentSpan, octal);
                        return 0;
                    }
                }
                catch (FormatException)
                {
                    _diagnostics.ReportInvalidOctal(CurrentSpan, octal);
                    return 0;
                }

                checked
                {
                    val += (long)(c * Math.Pow(8, j));
                }
            }

            return val;
        }

        private void ReadIdentifierOrKeyword()
        {
            var start = _charReader.Position;

            // Skip first letter
            NextChar();

            // The following characters can be letters, digits the underscore and the dollar sign.

            while (char.IsLetterOrDigit(_charReader.Current) ||
                   _charReader.Current == '_' ||
                   _charReader.Current == '$')
            {
                NextChar();
            }

            var end = _charReader.Position;
            var span = TextSpan.FromBounds(Text, start, end);
            var text = Text.GetText(span);

            if (_mode == LexerMode.UnityCgProgramSyntax && text == "ENDCG")
            {
                _kind = SyntaxKind.UnityEndCgKeyword;
            }
            else
            {
                _kind = _mode == LexerMode.UnitySyntax
                    ? SyntaxFacts.GetUnityKeywordKind(text)
                    : SyntaxFacts.GetKeywordKind(text);
            }

            _contextualKind = _mode == LexerMode.Directive
                ? SyntaxFacts.GetPreprocessorKeywordKind(text) 
                : SyntaxFacts.GetContextualKeywordKind(text);

            switch (_kind)
            {
                case SyntaxKind.TrueKeyword:
                    _value = true;
                    break;
                case SyntaxKind.FalseKeyword:
                    _value = false;
                    break;
                default:
                    _value = text;
                    break;
            }
        }
    }
}