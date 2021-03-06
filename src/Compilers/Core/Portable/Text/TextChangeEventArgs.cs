﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Text
{
    /// <summary>
    /// Represents state for a TextChanged event.
    /// </summary>
    public class TextChangeEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes an instance of <see cref="TextChangeEventArgs"/>.
        /// </summary>
        /// <param name="oldText">The text before the change.</param>
        /// <param name="newText">The text after the change.</param>
        /// <param name="changes">A non-empty set of ranges for the change.</param>
        public TextChangeEventArgs(SourceText oldText, SourceText newText, IEnumerable<TextChangeRange> changes)
        {
            if (changes == null || changes.IsEmpty())
            {
                throw new ArgumentException("changes");
            }

            this.OldText = oldText;
            this.NewText = newText;
            this.Changes = changes.ToImmutableArray();
        }

        /// <summary>
        /// Initializes an instance of <see cref="TextChangeEventArgs"/>.
        /// </summary>
        /// <param name="oldText">The text before the change.</param>
        /// <param name="newText">The text after the change.</param>
        /// <param name="changes">A non-empty set of ranges for the change.</param>
        public TextChangeEventArgs(SourceText oldText, SourceText newText, params TextChangeRange[] changes)
            : this(oldText, newText, (IEnumerable<TextChangeRange>)changes)
        {
        }

        /// <summary>
        /// Gets the text before the change.
        /// </summary>
        public SourceText OldText { get; private set; }

        /// <summary>
        /// Gets the text after the change.
        /// </summary>
        public SourceText NewText { get; private set; }

        /// <summary>
        /// Gets the set of ranges for the change.
        /// </summary>
        public IReadOnlyList<TextChangeRange> Changes { get; private set; }
    }
}
