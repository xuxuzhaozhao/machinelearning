// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Float = System.Single;

using System.Collections.Generic;
using System.IO;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Model;

namespace Microsoft.ML.Runtime.Api
{
    /// <summary>
    /// A class that runs the previously trained model (and the preceding transform pipeline) on the
    /// in-memory data in batch mode.
    /// This can also be used with trained pipelines that do not end with a predictor: in this case, the
    /// 'prediction' will be just the outcome of all the transformations.
    /// </summary>
    /// <typeparam name="TSrc">The user-defined type that holds the example.</typeparam>
    /// <typeparam name="TDst">The user-defined type that holds the prediction.</typeparam>
    public sealed class BatchPredictionEngine<TSrc, TDst>
        where TSrc : class
        where TDst : class, new()
    {
        // The source data view.
        private readonly DataViewConstructionUtils.StreamingDataView<TSrc> _srcDataView;
        // The transformation engine.
        private readonly PipeEngine<TDst> _pipeEngine;

        internal BatchPredictionEngine(IHostEnvironment env, Stream modelStream, bool ignoreMissingColumns,
            SchemaDefinition inputSchemaDefinition = null, SchemaDefinition outputSchemaDefinition = null)
        {
            Contracts.AssertValue(env);
            Contracts.AssertValue(modelStream);
            Contracts.AssertValueOrNull(inputSchemaDefinition);
            Contracts.AssertValueOrNull(outputSchemaDefinition);

            // Initialize pipe.
            _srcDataView = DataViewConstructionUtils.CreateFromEnumerable(env, new TSrc[] { }, inputSchemaDefinition);

            // Load transforms.
            var pipe = env.LoadTransforms(modelStream, _srcDataView);

            // Load predictor (if present) and apply default scorer.
            // REVIEW: distinguish the case of predictor / no predictor?
            var predictor = env.LoadPredictorOrNull(modelStream);
            if (predictor != null)
            {
                var roles = ModelFileUtils.LoadRoleMappingsOrNull(env, modelStream);
                pipe = roles != null
                    ? env.CreateDefaultScorer(RoleMappedData.CreateOpt(pipe, roles), predictor)
                    : env.CreateDefaultScorer(env.CreateExamples(pipe, "Features"), predictor);
            }

            _pipeEngine = new PipeEngine<TDst>(env, pipe, ignoreMissingColumns, outputSchemaDefinition);
        }

        internal BatchPredictionEngine(IHostEnvironment env, IDataView dataPipeline, bool ignoreMissingColumns,
           SchemaDefinition inputSchemaDefinition = null, SchemaDefinition outputSchemaDefinition = null)
        {
            Contracts.AssertValue(env);
            Contracts.AssertValue(dataPipeline);
            Contracts.AssertValueOrNull(inputSchemaDefinition);
            Contracts.AssertValueOrNull(outputSchemaDefinition);

            // Initialize pipe.
            _srcDataView = DataViewConstructionUtils.CreateFromEnumerable(env, new TSrc[] { }, inputSchemaDefinition);
            var pipe = ApplyTransformUtils.ApplyAllTransformsToData(env, dataPipeline, _srcDataView);

            _pipeEngine = new PipeEngine<TDst>(env, pipe, ignoreMissingColumns, outputSchemaDefinition);
        }

        /// <summary>
        /// Run the prediction pipe. This will enumerate the <paramref name="examples"/> exactly once, 
        /// cache all the examples (by reference) into its internal representation and then run 
        /// the transformation pipe.
        /// </summary>
        /// <param name="examples">The examples to run the prediction on.</param>
        /// <param name="reuseRowObjects">If <c>true</c>, the engine will not allocate memory per output, and 
        /// the returned <typeparamref name="TDst"/> objects will actually always be the same object. The user is
        /// expected to clone the values himself if needed.</param>
        /// <returns>The <see cref="IEnumerable{TDst}"/> that contains all the pipeline results.</returns>
        public IEnumerable<TDst> Predict(IEnumerable<TSrc> examples, bool reuseRowObjects)
        {
            Contracts.CheckValue(examples, nameof(examples));

            _pipeEngine.Reset();
            _srcDataView.SetData(examples);
            return _pipeEngine.RunPipe(reuseRowObjects);
        }
    }

    /// <summary>
    /// Utility class to run the pipeline to completion and produce a strongly-typed IEnumerable as a result.
    /// Doesn't allocate memory for every row: instead, yields the same row object on every step.
    /// </summary>
    internal sealed class PipeEngine<TDst>
        where TDst : class, new()
    {
        private readonly ICursorable<TDst> _cursorablePipe;
        private long _counter;

        internal PipeEngine(IHostEnvironment env, IDataView pipe, bool ignoreMissingColumns, SchemaDefinition schemaDefinition = null)
        {
            Contracts.AssertValue(env);
            env.AssertValue(pipe);
            env.AssertValueOrNull(schemaDefinition);

            _cursorablePipe = pipe.AsCursorable<TDst>(env, ignoreMissingColumns, schemaDefinition);
            _counter = 0;
        }

        public IEnumerable<TDst> RunPipe(bool reuseRowObject)
        {
            var curCounter = _counter;
            using (var cursor = _cursorablePipe.GetCursor())
            {
                TDst row = null;
                while (cursor.MoveNext())
                {
                    if (!reuseRowObject || row == null)
                        row = new TDst();

                    cursor.FillValues(row);
                    yield return row;
                    if (curCounter != _counter)
                        throw Contracts.Except("An attempt was made to keep iterating after the pipe has been reset.");
                }
            }
        }

        public void Reset()
        {
            _counter++;
        }
    }

    /// <summary>
    /// A class that runs the previously trained model (and the preceding transform pipeline) on the
    /// in-memory data, one example at a time.
    /// This can also be used with trained pipelines that do not end with a predictor: in this case, the
    /// 'prediction' will be just the outcome of all the transformations.
    /// This is essentially a wrapper for <see cref="BatchPredictionEngine{TSrc,TDst}"/> that throws if 
    /// more than one result is returned per call to <see cref="Predict"/>.
    /// </summary>
    /// <typeparam name="TSrc">The user-defined type that holds the example.</typeparam>
    /// <typeparam name="TDst">The user-defined type that holds the prediction.</typeparam>
    public sealed class PredictionEngine<TSrc, TDst>
        where TSrc : class
        where TDst : class, new()
    {
        private readonly BatchPredictionEngine<TSrc, TDst> _engine;

        internal PredictionEngine(IHostEnvironment env, Stream modelStream, bool ignoreMissingColumns,
            SchemaDefinition inputSchemaDefinition = null, SchemaDefinition outputSchemaDefinition = null)
        {
            Contracts.CheckValue(env, nameof(env));
            var singleThreadedEnv = env.Register("SingleThreaded", conc: 1);
            _engine = new BatchPredictionEngine<TSrc, TDst>(singleThreadedEnv, modelStream, ignoreMissingColumns,
                inputSchemaDefinition, outputSchemaDefinition);
        }

        internal PredictionEngine(IHostEnvironment env, IDataView dataPipe, bool ignoreMissingColumns,
            SchemaDefinition inputSchemaDefinition = null, SchemaDefinition outputSchemaDefinition = null)
        {
            Contracts.CheckValue(env, nameof(env));
            var singleThreadedEnv = env.Register("SingleThreaded", conc: 1);
            _engine = new BatchPredictionEngine<TSrc, TDst>(singleThreadedEnv, dataPipe, ignoreMissingColumns,
                inputSchemaDefinition, outputSchemaDefinition);
        }

        /// <summary>
        /// Run prediction pipeline on one example.
        /// </summary>
        /// <param name="example">The example to run on.</param>
        /// <returns>The result of prediction. A new object is created for every call.</returns>
        public TDst Predict(TSrc example)
        {
            Contracts.CheckValue(example, nameof(example));
            int count = 0;
            TDst result = null;
            foreach (var item in _engine.Predict(new[] { example }, true))
            {
                if (count == 0)
                    result = item;

                count++;
                if (count > 1)
                    break;
            }

            if (count > 1)
                throw Contracts.Except("Prediction pipeline must return at most one prediction per example. If it isn't, use BatchPredictionEngine.");
            return result;
        }
    }

    /// <summary>
    /// This class encapsulates the 'classic' prediction problem, where the input is denoted by the float array of features,
    /// and the output is a float score. For binary classification predictors that can output probability, there are output 
    /// fields that report the predicted label and probability.
    /// </summary>
    public sealed class SimplePredictionEngine
    {
        private class Example
        {
            // REVIEW: convert to VBuffer once we have support for them.
            public Float[] Features;
        }

        /// <summary>
        /// The prediction output. For every field, if there are no column with the matched name in the scoring pipeline,
        /// the field will be left intact by the engine (and keep 0 as value unless the user code changes it).
        /// </summary>
        public class Prediction
        {
            public Float Score;
            public Float Probability;
        }

        private readonly PredictionEngine<Example, Prediction> _engine;
        private readonly int _nFeatures;

        /// <summary>
        /// Create a prediction engine.
        /// </summary>
        /// <param name="env">The host environment to use.</param>
        /// <param name="modelStream">The model stream to load pipeline from.</param>
        /// <param name="nFeatures">Number of features.</param>
        /// <param name="featureColumnName">Name of the features column.</param>
        internal SimplePredictionEngine(IHostEnvironment env, Stream modelStream, int nFeatures, string featureColumnName = "Features")
        {
            Contracts.AssertValue(env);
            Contracts.AssertValue(modelStream);
            Contracts.Assert(nFeatures > 0);

            _nFeatures = nFeatures;
            var schema =
                new SchemaDefinition
                {
                new SchemaDefinition.Column
                {
                        MemberName = featureColumnName,
                        ColumnType = new VectorType(NumberType.Float, nFeatures)
                }
            };
            _engine = new PredictionEngine<Example, Prediction>(env, modelStream, true, schema);
        }

        /// <summary>
        /// Score an example.
        /// </summary>
        /// <param name="features">The feature array of the example.</param>
        /// <returns>The prediction object. New object is created on every call.</returns>
        public Prediction Predict(Float[] features)
        {
            Contracts.CheckValue(features, nameof(features));
            if (features.Length != _nFeatures)
                throw Contracts.ExceptParam(nameof(features), "Number of features should be {0}, but it is {1}", _nFeatures, features.Length);

            var example = new Example { Features = features };
            return _engine.Predict(example);
        }
        public Prediction Predict(VBuffer<Float> features)
        {
            throw Contracts.ExceptNotImpl("VBuffers aren't supported yet.");
        }
    }
}